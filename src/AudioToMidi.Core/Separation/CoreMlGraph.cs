using Google.Protobuf;
using Microsoft.ML.OnnxRuntime;
using Onnx;

namespace AudioToMidi.Core.Separation;

/// <summary>
/// Rewrite an ONNX graph so ONNX Runtime's CoreML execution provider can run all of it as one CoreML
/// model. CoreML rejects some ops, and every tensor with a zero-length dimension. Each rejected node cuts
/// the graph, and a graph cut into many pieces runs far slower than on the CPU. Every rewrite computes the
/// same values with ops CoreML accepts.
/// </summary>
static class CoreMlGraph
{
    const int Float = 1, Int64 = 7;

    /// <summary>Rewrite the model at <paramref name="modelPath"/> and return it as ONNX bytes.</summary>
    /// <param name="cacheKey">Written to the graph's metadata as COREML_CACHE_KEY: the name ONNX Runtime
    /// gives the compiled CoreML model in its cache (see <see cref="GpuModelCache"/>).</param>
    public static byte[] Prepare(string modelPath, string? cacheKey = null)
    {
        // ONNX Runtime writes the graph out with every intermediate tensor's shape, which the rewrites need.
        var annotated = Path.Combine(Path.GetTempPath(), $"a2m-coreml-{Guid.NewGuid():N}.onnx");
        try
        {
            using (var options = new SessionOptions { GraphOptimizationLevel = GraphOptimizationLevel.ORT_DISABLE_ALL, OptimizedModelFilePath = annotated, LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_ERROR })
            using (new InferenceSession(modelPath, options)) { }
            ModelProto model;
            using (var stream = File.OpenRead(annotated)) model = ModelProto.Parser.ParseFrom(stream);
            int opset = model.OpsetImport.Where(o => o.Domain is "" or "ai.onnx").Select(o => (int)o.Version).DefaultIfEmpty(0).Max();
            if (opset < 13) throw new NotSupportedException($"Opset {opset}: the rewrites need opset 13 or later.");
            Rewrite(model.Graph);
            if (cacheKey is not null)
            {
                foreach (var old in model.MetadataProps.Where(p => p.Key == "COREML_CACHE_KEY").ToList()) model.MetadataProps.Remove(old);
                model.MetadataProps.Add(new StringStringEntryProto { Key = "COREML_CACHE_KEY", Value = cacheKey });
            }
            return model.ToByteArray();
        }
        finally
        {
            File.Delete(annotated);
        }
    }

    static void Rewrite(GraphProto g)
    {
        var shapes = new Dictionary<string, long[]>();
        var types = new Dictionary<string, int>();
        foreach (var vi in g.ValueInfo.Concat(g.Input).Concat(g.Output))
        {
            if (vi.Type?.TensorType is not { } t) continue;
            types[vi.Name] = t.ElemType;
            if (t.Shape is { } s) shapes[vi.Name] = s.Dim.Select(d => d.ValueCase == TensorShapeProto.Types.Dimension.ValueOneofCase.DimValue ? d.DimValue : -1).ToArray();
        }
        var constants = g.Initializer.ToDictionary(t => t.Name);
        foreach (var n in g.Node.Where(n => n.OpType == "Constant"))
            if (n.Attribute.FirstOrDefault(a => a.Name == "value")?.T is { } t) constants[n.Output[0]] = t;
        var graphOutputs = g.Output.Select(o => o.Name).ToHashSet();
        var rename = new Dictionary<string, string>();
        int uid = 0;

        bool Empty(string name) => shapes.TryGetValue(name, out var s) && s.Contains(0);
        string Constant(string owner, TensorProto value)
        {
            value.Name = $"{owner}/coreml_{++uid}";
            g.Initializer.Add(value);
            return value.Name;
        }
        string Longs(string owner, params long[] values) => Constant(owner, LongTensor(values));
        string Fresh(string owner) => $"{owner}/coreml_{++uid}";

        var nodes = new List<NodeProto>();
        foreach (var n in g.Node)
        {
            switch (n.OpType)
            {
                // Empty inputs add nothing to a concatenation; with one input left it is that input.
                case "Concat":
                {
                    var keep = n.Input.Where(i => !Empty(i)).ToList();
                    if (keep.Count == 1 && !graphOutputs.Contains(n.Output[0]))
                    {
                        rename[n.Output[0]] = keep[0];
                        continue;
                    }
                    if (keep.Count > 1 && keep.Count < n.Input.Count)
                    {
                        n.Input.Clear();
                        n.Input.Add(keep);
                    }
                    break;
                }
                case "Neg" when types.GetValueOrDefault(n.Input[0]) == Float:
                    nodes.Add(Node("Mul", n.Name + "/as_mul", [n.Input[0], Constant(n.Name, FloatTensor(-1f))], [n.Output[0]]));
                    continue;
                // Attention: q·kᵀ and weights·v.
                case "Einsum" when EinsumEquation(n) is "bhid,bhjd->bhij":
                {
                    var kT = Fresh(n.Name);
                    var transpose = Node("Transpose", kT, [n.Input[1]], [kT]);
                    transpose.Attribute.Add(new AttributeProto { Name = "perm", Type = AttributeProto.Types.AttributeType.Ints, Ints = { 0, 1, 3, 2 } });
                    nodes.Add(transpose);
                    nodes.Add(Node("MatMul", n.Name + "/as_matmul", [n.Input[0], kT], [n.Output[0]]));
                    continue;
                }
                case "Einsum" when EinsumEquation(n) is "bhij,bhjd->bhid":
                    nodes.Add(Node("MatMul", n.Name + "/as_matmul", [n.Input[0], n.Input[1]], [n.Output[0]]));
                    continue;
                // ‖x‖ = √Σx², from ops CoreML has.
                case "ReduceL2" when n.Input.Count == 1 && n.Attribute.FirstOrDefault(a => a.Name == "axes") is { } axes:
                {
                    long keepDims = n.Attribute.FirstOrDefault(a => a.Name == "keepdims")?.I ?? 1;
                    string square = Fresh(n.Name), sum = Fresh(n.Name);
                    nodes.Add(Node("Mul", square, [n.Input[0], n.Input[0]], [square]));
                    var reduce = Node("ReduceSum", sum, [square, Longs(n.Name, [.. axes.Ints])], [sum]);
                    reduce.Attribute.Add(new AttributeProto { Name = "keepdims", Type = AttributeProto.Types.AttributeType.Int, I = keepDims });
                    nodes.Add(reduce);
                    nodes.Add(Node("Sqrt", n.Name + "/sqrt", [sum], [n.Output[0]]));
                    continue;
                }
                // Pick one index as a one-wide slice, then squeeze it (CoreML's gather takes at most rank 4 here).
                case "Gather" when constants.TryGetValue(n.Input[1], out var index) && index.Dims.Count == 0 && index.DataType == Int64:
                {
                    long k = ReadLong(index);
                    long axis = n.Attribute.FirstOrDefault(a => a.Name == "axis")?.I ?? 0;
                    var slice = Fresh(n.Name);
                    nodes.Add(Node("Slice", slice, [n.Input[0], Longs(n.Name, k), Longs(n.Name, k == -1 ? long.MaxValue : k + 1), Longs(n.Name, axis)], [slice]));
                    nodes.Add(Node("Squeeze", n.Name + "/squeeze", [slice, Longs(n.Name, axis)], [n.Output[0]]));
                    continue;
                }
            }
            nodes.Add(n);
        }

        // Drop an Expand that only feeds element-wise ops which broadcast anyway (as torch's normalize does),
        // when broadcasting without it gives the same shape.
        var consumers = nodes.SelectMany(n => n.Input.Select(i => (i, n))).ToLookup(x => x.i, x => x.n);
        var elementwise = new HashSet<string> { "Add", "Sub", "Mul", "Div" };
        nodes.RemoveAll(n =>
        {
            if (n.OpType != "Expand" || graphOutputs.Contains(n.Output[0]) || !shapes.TryGetValue(n.Input[0], out var inShape)) return false;
            var users = consumers[n.Output[0]].ToList();
            if (users.Count == 0 || !users.All(u => elementwise.Contains(u.OpType))) return false;
            foreach (var u in users)
            {
                if (!shapes.TryGetValue(u.Output[0], out var outShape)) return false;
                long[]? shape = inShape;
                foreach (var other in u.Input.Where(i => i != n.Output[0]))
                {
                    if (!shapes.TryGetValue(other, out var s)) return false;
                    shape = Broadcast(shape, s);
                    if (shape is null) return false;
                }
                if (!shape.SequenceEqual(outShape)) return false;
            }
            rename[n.Output[0]] = n.Input[0];
            return true;
        });

        foreach (var n in nodes)
            for (int k = 0; k < n.Input.Count; k++)
            {
                var name = n.Input[k];
                while (rename.TryGetValue(name, out var to)) name = to;
                n.Input[k] = name;
            }

        // Drop what nothing reads any more. Shapes are worked out again on load.
        while (true)
        {
            var read = nodes.SelectMany(n => n.Input).Concat(graphOutputs).ToHashSet();
            if (nodes.RemoveAll(n => !n.Output.Any(o => o.Length > 0 && read.Contains(o))) == 0) break;
        }
        var used = nodes.SelectMany(n => n.Input).ToHashSet();
        var initializers = g.Initializer.Where(t => used.Contains(t.Name)).ToList();
        g.Initializer.Clear();
        g.Initializer.Add(initializers);
        g.Node.Clear();
        g.Node.Add(nodes);
        g.ValueInfo.Clear();
    }

    static string? EinsumEquation(NodeProto n) =>
        n.Attribute.FirstOrDefault(a => a.Name == "equation")?.S.ToStringUtf8().Replace(" ", "");

    /// <summary>Broadcast two known shapes as NumPy does; return null if a dimension is unknown or they do not broadcast.</summary>
    static long[]? Broadcast(long[] a, long[] b)
    {
        int rank = Math.Max(a.Length, b.Length);
        var result = new long[rank];
        for (int i = 0; i < rank; i++)
        {
            long x = i < rank - a.Length ? 1 : a[i - (rank - a.Length)];
            long y = i < rank - b.Length ? 1 : b[i - (rank - b.Length)];
            if (x < 0 || y < 0 || (x != y && x != 1 && y != 1)) return null;
            result[i] = x == 1 ? y : x;
        }
        return result;
    }

    static NodeProto Node(string op, string name, string[] inputs, string[] outputs)
    {
        var node = new NodeProto { OpType = op, Name = name };
        node.Input.Add(inputs);
        node.Output.Add(outputs);
        return node;
    }

    static TensorProto FloatTensor(float value) =>
        new() { DataType = Float, RawData = ByteString.CopyFrom(BitConverter.GetBytes(value)) };

    static TensorProto LongTensor(long[] values)
    {
        var t = new TensorProto { DataType = Int64, Dims = { values.Length } };
        t.RawData = ByteString.CopyFrom(values.SelectMany(BitConverter.GetBytes).ToArray());
        return t;
    }

    static long ReadLong(TensorProto t) =>
        t.RawData.Length >= 8 ? BitConverter.ToInt64(t.RawData.Span) : t.Int64Data[0];
}
