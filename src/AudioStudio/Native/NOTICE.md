# Audio dependencies

OneBox implements its own audio routing, DSP orchestration and UI. No MeowMic code is used.

DeepFilterNet3 low-latency model/runtime: Rikorose/DeepFilterNet v0.5.6, MIT / Apache-2.0.
Official unmodified Windows x64 LADSPA release:
https://github.com/Rikorose/DeepFilterNet/releases/download/v0.5.6/deep_filter_ladspa-0.5.6-x86_64-pc-windows-msvc.dll
SHA256: F97E34275EB884B6076A96D261683D2A43C6B2D97DDF9E346BC06F7C8057EB04
The DLL contains the upstream low-latency DFN3 model. It runs only inside a OneBox child process.

RNNoise: Xiph / Mozilla (BSD-3-Clause), through YellowDogMan.RRNoise.NET 0.1.9 (MIT), restored by NuGet.
https://github.com/Yellow-Dog-Man/RNNoise.NET
https://github.com/xiph/rnnoise

VB-CABLE is not distributed or installed by OneBox. The setup guide opens the author's website:
https://vb-audio.com/Cable/
