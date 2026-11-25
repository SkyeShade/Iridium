# Third-party notices

## Unicode Emoji 17.0 data

Iridium includes `emoji-test.txt` from the Unicode Emoji 17.0 data files. Unicode data files are
provided under the Unicode License v3 (SPDX: Unicode-3.0). The license is included at
`Iridium.Protocol/UNICODE-LICENSE.txt`.

## Twemoji v17.0.3

Iridium bundles commonly used Twemoji SVG artwork and lazy-loads other standard emoji artwork from
the same pinned release via jsDelivr. The artwork originates from
[jdecked/twemoji](https://github.com/jdecked/twemoji/tree/v17.0.3).

The Twemoji graphics are licensed under the Creative Commons Attribution 4.0 International license
(CC BY 4.0). Copyright 2022-present Jason Sofonia and Justine De Caires; copyright 2014-2021 Twitter.
The complete graphics license is included at
`Iridium.Web/wwwroot/vendor/twemoji/LICENSE-GRAPHICS`.

Twemoji software is licensed under the MIT License. Its license is included at
`Iridium.Web/wwwroot/vendor/twemoji/LICENSE-CODE`.

## web-noise-suppressor 0.4.2 and RNNoise

Iridium bundles the RNNoise worklet and WebAssembly builds from
[sapphi-red/web-noise-suppressor](https://github.com/sapphi-red/web-noise-suppressor) 0.4.2 in
`Iridium.Web/wwwroot/lib/web-noise-suppressor` for enhanced noise suppression in voice.

- web-noise-suppressor is licensed under the MIT License. Copyright (c) 2022 翠 / green. The license is
  included at `Iridium.Web/wwwroot/lib/web-noise-suppressor/LICENSE`.
- Its RNNoise build comes from [shiguredo/rnnoise-wasm](https://github.com/shiguredo/rnnoise-wasm),
  licensed under the Apache License 2.0 (copyright Shiguredo Inc.). The license is included at
  `Iridium.Web/wwwroot/lib/web-noise-suppressor/LICENSE-RNNOISE-WASM`.
- [RNNoise](https://github.com/xiph/rnnoise) is licensed under the BSD 3-Clause License. Copyright (c)
  2007-2017, 2024 Jean-Marc Valin, 2023 Amazon, 2017 Mozilla, 2005-2017 Xiph.Org Foundation and
  2003-2004 Mark Borgerding. The license is included at
  `Iridium.Web/wwwroot/lib/web-noise-suppressor/LICENSE-RNNOISE`.
