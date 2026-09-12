# RAW render policy v1

LibRaw 0.22.2 through the version 1 FolderLens C ABI. Open/unpack/process/make-memory-image is the developed path; unpack-thumbnail/make-memory-thumbnail is independently labelled embedded preview. Never infer full quality from preview dimensions.

The fixed developed policy is camera white balance, no automatic white balance or automatic brightness, full resolution, AHD demosaicing (user_qual=3), 16-bit sRGB output, sRGB transfer parameters, LibRaw orientation. No user aesthetic adjustments. The worker normalizes/resamples before final 8-bit SDR display. Native processed memory is released by its owning RawSession; cancellation sets the LibRaw progress callback flag, with parent process termination as the hard timeout fallback.

Limits: 300 million effective pixels; raw allocation limit passed by host (256–6144 MiB). Source file is never written. Colour reference and exact camera validation remain required; this policy document is not a quality acceptance result.
