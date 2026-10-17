# Model licences

The models AudioToMidi uses come with their own terms, separate from the app's licence. The ones
built into the app are under permissive licences. The instrument separation models are not: users
download them from their publishers, or install them from a separate model archive that carries
this file. Checked October 2026; none of this is legal
advice.

| Model | File | How the app gets it | Terms | Redistribute? |
|---|---|---|---|---|
| [basic-pitch](https://github.com/spotify/basic-pitch) (Spotify) | `nmp.onnx` | built in | Apache-2.0, © 2022 Spotify AB | Yes, with the Apache-2.0 text and the NOTICE below |
| [YAMNet](https://www.kaggle.com/models/google/yamnet) (Google), ONNX conversion by [zeropointnine](https://huggingface.co/zeropointnine/yamnet-onnx), mirrored by [andrelgomes](https://huggingface.co/andrelgomes/yamnet-onnx) | `yamnet.onnx` | built in | Apache-2.0, Google; converted from TensorFlow to ONNX | Yes, with the Apache-2.0 text |
| YAMNet class map ([tensorflow/models](https://github.com/tensorflow/models/tree/master/research/audioset/yamnet)) | `yamnet_class_map.csv` | built in | Apache-2.0 file; its labels come from the [AudioSet ontology](https://github.com/audioset/ontology), CC BY-SA 4.0, Google | Yes, unmodified, with both attributions |
| Instrument classifier | `instrument_head.json` | built in | Trained here on GarageBand's Apple Loops | Check the GarageBand licence first |
| [HT-Demucs](https://github.com/facebookresearch/demucs) 4- and 6-source (Meta), ONNX exports by [StemSplitio](https://huggingface.co/StemSplitio/htdemucs-onnx) | `htdemucs_fp16weights.onnx`, `htdemucs_6s_fp16weights.onnx` | download, or the model archive | Code: MIT, © Meta Platforms, Inc. Weights: no licence file; the author says they are not covered by the MIT licence and are "provided only for scientific purposes" ([#327](https://github.com/facebookresearch/demucs/issues/327), [#384](https://github.com/facebookresearch/demucs/issues/384)), as they were trained on MusDB. StemSplitio labels its exports MIT, but cannot grant more than Meta did | Only in the model archive, for personal and research use |
| BS-RoFormer SW 6-stem, [ONNX export by Eli White](https://huggingface.co/elicwhite/bs-roformer-sw-6stem-onnx) | `bs_roformer_sw_6stem_fp16.onnx` | optional download by the user | None for the weights. The export code is MIT, but the weights were rehosted without a licence, and community guides report they were taken from Apple's Logic Pro stem splitter, whose licence forbids reverse engineering. The band-split transformer method is patented (US 12,542,148 B2, Lemon Inc.) | **No.** Not in the archive, not in the app; users download it themselves for personal experiments |

SoundFonts are not models, but the same applies: the app ships none. Users choose their own (the
menu links to free ones, such as GeneralUser GS, whose licence allows any use and asks that people
link to [its page](https://www.schristiancollins.com/generaluser.php) rather than to its download).

## Notices

basic-pitch's NOTICE file, reproduced as its licence requires:

```
Basic Pitch
Copyright 2022 Spotify AB

This product includes software developed at
Spotify AB (http://www.spotify.com/).

This product includes software from Librosa (ISC).
* Copyright (C) 2013--2017, librosa development team.

This product includes software from mir_eval (MIT)
* Copyright (C) 2014 Colin Raffel

This product includes software from numpy (BSD)
* Copyright (C) 2005-2022, NumPy Developers.

This product includes software from pretty-midi (MIT)
* Copyright (C) 2014 Colin Raffel

This product includes software from resampy (ISC)
* Copyright (C) 2016, Brian McFee

This product includes software from scipy (BSD)
* Copyright (C) 2001-2002 Enthought, Inc. 2003-2022, SciPy Developers

This product includes software from tensorflow (Apache 2.0)
* Copyright (C) 2019 Google, LLC <packages@tensorflow.org>

The tests for `basic-pitch` include audio files from the
Vocadito dataset liscened under Creative Commons
Attribution 4.0 International. The dataset can be found at:
https://zenodo.org/record/5578807#.YnRm5vPMKDU
```

HT-Demucs: Rouard, Massa and Défossez, "Hybrid Transformers for Music Source Separation", ICASSP
2023. Demucs code: MIT licence, Copyright (c) Meta Platforms, Inc. and affiliates.

AudioSet ontology by Google, licensed under CC BY-SA 4.0
(https://creativecommons.org/licenses/by-sa/4.0/).

The Apache License 2.0 is at https://www.apache.org/licenses/LICENSE-2.0.
