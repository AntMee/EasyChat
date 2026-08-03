# Shared MicroASR VAD

`svad.quantized.onnx` is the locale-neutral voice activity detector shared by all
MicroASR language models. Upstream distributes it in the `en-US` archive from the
[`models-v1` release](https://github.com/SwaggyMacro/MicroASR/releases/tag/models-v1),
while the other locale archives rely on the common model-library root to provide it.

The shared Desktop host publishes this file into the runtime `Models` directory so a
single non-English archive can be imported on a clean installation on every platform.

SHA-256: `31A098E19F2584752698A4F72A527CA13EFADFB50A018C42EB0C78209B1FAD81`
