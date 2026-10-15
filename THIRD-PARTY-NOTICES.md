# Third-party notices

Simple Bike Trainer is licensed under the GNU General Public License v3.0 only (SPDX `GPL-3.0-only`, see [LICENSE](LICENSE)). Parts of it are based on the projects below.

## BikeControl, formerly SwiftControl (GPL-3.0)

[src/Trainer.Core/Devices/ZwiftClickDecoder.cs](src/Trainer.Core/Devices/ZwiftClickDecoder.cs) and the Zwift controller handling in [src/Trainer.Core/Ble](src/Trainer.Core/Ble) follow [OpenBikeControl/bikecontrol](https://github.com/OpenBikeControl/bikecontrol), which began as jonasbark/swiftcontrol. That project was GPL-3.0 up to its `gpl3` tag (2026-01-31) and uses a non-commercial license after it. The code here follows the GPL-3.0 versions.

One fact comes from a later version: controller type `0x0E` in the scan advertisement marks a Zwift Play on firmware 2.x ([BleScanner.cs](src/Trainer.Core/Ble/BleScanner.cs)). This is a protocol value, not code.

## python-garminconnect (MIT)

[src/Trainer.Core/Uploads/GarminClient.cs](src/Trainer.Core/Uploads/GarminClient.cs) follows the Garmin Connect sign-in flow of [cyberjunky/python-garminconnect](https://github.com/cyberjunky/python-garminconnect) 0.3.17.

```text
MIT License

Copyright (c) 2020-2026 Ron Klinkien

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

## Zwift Play research

The Zwift controller protocol notes in [ajchellew/zwiftplay](https://github.com/ajchellew/zwiftplay) informed the decoder. That repository has no license, so no code from it is included; only protocol facts (service UUIDs, message types, button layout) are used.
