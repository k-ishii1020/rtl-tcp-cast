# RTL-TCP-CAST
RTL-SDRデバイスを統合管理し、複数クライアントに分配・配信できるアプリです。
アプリごとにドングルを用意したり、アンテナを分配したりせず、同じ受信データを複数の用途に使えます。

[日本語の使い方 / Japanese guide](https://k-ishii1020.github.io/rtl-tcp-cast/guide.html#ja)

## 免責事項 / Disclaimer

本ソフトウェアは「現状有姿」で提供され、明示または黙示を問わず、商品性、特定目的への適合性、正確性、継続的な動作などを保証しません。利用および結果の確認は利用者の責任で行ってください。適用法令で認められる最大限の範囲において、作者および著作権者は、本ソフトウェアの使用または使用不能に起因する損害（データの消失、機器や他のソフトウェアへの損害、業務の中断、第三者との紛争などを含みます）について責任を負いません。ただし、適用法令により免責できない責任を除きます。利用者は、受信・録音・配信に関係する法令、機器や受信アプリの利用条件、および第三者ソフトウェアのライセンスを遵守してください。

This software is provided “as is”, without warranties of any kind, express or implied, including merchantability, fitness for a particular purpose, accuracy, or uninterrupted operation. You are responsible for evaluating and using it. To the maximum extent permitted by applicable law, the author and copyright holders will not be liable for damages arising from using, or being unable to use, this software, including loss of data, damage to equipment or other software, business interruption, or third-party claims, except where such liability cannot be excluded by law. Users are responsible for complying with applicable laws, terms governing their hardware and receiver applications, and third-party software licenses.

## License

RTL-TCP-CAST is distributed under the **GNU General Public License, version 2 or (at your option) any later version (GPL-2.0-or-later)**. Copyright (c) 2026 いしかず. This software is provided without warranty. Redistribution is subject to the terms of the GPL.

See [LICENSE](LICENSE) for the complete license text and [NOTICE](NOTICE) for copyright and warranty notices. The application, source code, and build scripts are licensed under GPL-2.0-or-later.

### Binary redistribution

When redistributing a binary, make the corresponding source code and build instructions available from the same place as the binary. For GitHub Releases, tag the exact commit used for the build and provide that tag's source archive. Include all source files needed to build the distributed binary.

### Included software

The distribution ZIP includes a single `RTL-TCP-Cast.exe` with the .NET / WPF runtime bundled inside. Runtime licenses and third-party notices are in `licenses/` inside the ZIP. External RTL-SDR software, including rtl_tcp, rtlsdr.dll / librtlsdr.dll, and USB drivers, is not bundled.

License details: [Third-party license notices](docs/LICENSES.md)
