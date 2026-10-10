# ライセンスと配布方針

確認日: 2026-10-10

## 配布物

RTL-TCP-CASTはWindows x64向けの自己完結型フォルダーをZIPで配布します。インストーラーや.NETの事前インストールは不要です。ZIPをすべて展開してRTL-TCP-Cast.exeを起動します。exe単体の配布ではありません。

追加のNuGetライブラリへのPackageReferenceはありません。配布物には独自のC#実装と.NET / WPFランタイムが含まれます。

| 対象 | 方針 |
| --- | --- |
| 本体・付属ソース・文書・ビルドスクリプト | GPL-2.0-or-later。Copyright (c) 2026 いしかず。GPL全文をLICENSE.txt、著作権・適用宣言をNOTICE.txtとして同梱 |
| .NET / WPF | 発行で実際に使用したランタイムパッケージのライセンスをlicenses/に同梱。第三者通知も同梱 |
| rtl_tcp、rtlsdr.dll / librtlsdr.dll、関連DLL・ツール・USBドライバー | 同梱しない。利用者が別途導入 |
| C:\tools\rtl-sdr 内の全ファイル | 開発環境の外部ツール。配布対象外 |

SDKやビルド用ツール一式は同梱しません。WindowsのシステムDLLも同梱しません。

## 外部RTL-SDRライブラリ

TCP中継とrtl_tcpの起動は、別プロセスとの通信として実装されています。一方、SdrDevices.csとSdrEeprom.csは利用者が指定したrtl_tcpと同じフォルダーのRTL-SDR DLLをアプリのプロセス内にロードし、デバイス列挙・EEPROM操作のAPIを呼び出します。

Osmocomの上流版rtl_tcp / librtlsdrはGPL-2.0-or-laterです。本体もGPL-2.0-or-laterで提供し、外部GPLライブラリの直接利用とライセンスを揃えます。DLLの非同梱を理由にGPLの条件を回避する方針ではありません。利用者が別の派生版DLLを使う場合は、その版の条件に従ってください。

配布元はRTL-SDR BlogのWindows Release V1.4.0と確認しました。Downloads/Release.zipのダウンロード元情報が同リリースを指しており、C:\tools\rtl-sdrの全16ファイルのSHA-256がZIP内のx64版と一致しています。rtlsdr.dllのバージョン情報はv0.6.0-131-gaed0で、リリースのコミットaed0ea1とも整合します。同タグのlibrtlsdr.cとrtl_tcp.cはGPL-2.0-or-laterです。

FSFのGPL FAQは、GPLコードとの静的・動的リンクを組み合わせたプログラムとして扱い、組み合わせの配布にはGPLの条件が必要と説明しています。一方、通常のパイプ・ソケット・コマンドラインによる通信は別プログラム間の通信と説明しています。これはFSFの解釈であり、今回の配布形態に対する裁判上の確定判断ではありません。

## リリースと対応ソース

1. Windows x64と.NET 10 SDKを用意し、READMEの手順で `scripts/build.ps1 -Publish` を実行します。
2. バイナリと対応ソースを同じリリース・配布場所で提供します。GitHub Releasesではビルド元のコミットにタグを付け、必要なファイルがすべて含まれる自動生成ソースアーカイブを利用できます。未コミットの変更を含むビルドにはそのタグのソースが対応しないため、コミットして再ビルドするか、発行時の作業ツリーから生成する `artifacts/RTL-TCP-Cast-source.zip` を添付します。別時点のソースや最新ブランチへのリンクだけで代用しないでください。
3. ソースZIPはsrc、tests、scripts、docs、ソリューション、ビルド設定、LICENSE、NOTICE、READMEを含みます。Git履歴、bin/obj、SDK、RTL-SDR外部バイナリ、利用者の設定は含みません。
4. バイナリZIPにはLICENSE.txt、NOTICE.txt、LICENSES.mdとlicenses/内の.NET文書を含めます。改変して配布する場合は変更内容・日付を明示し、改変版に対応するソースを提供してください。

リリースするソースZIPは、配布するバイナリと対になるものを保存してください。ビルド後にソースを変更した場合は両方を生成し直します。インターネット上で両者を同じ場所から取得できるようにする方式を採用し、書面による後日ソース提供の約束で代用しません。

現在の配布対象外のRTL-SDRファイルを将来同梱する場合は、それらのライセンス表示と対応ソースの提供も別途整備します。

## 参照

- [rtl_tcpのライセンス表記](https://github.com/osmocom/rtl-sdr/blob/master/src/rtl_tcp.c)
- [librtlsdrのライセンス表記](https://github.com/osmocom/rtl-sdr/blob/master/src/librtlsdr.c)
- [.NET Runtimeのライセンス](https://github.com/dotnet/runtime/blob/main/LICENSE.TXT)
- [.NET Runtimeの第三者通知](https://github.com/dotnet/runtime/blob/main/THIRD-PARTY-NOTICES.TXT)
- [WPFのライセンス](https://github.com/dotnet/wpf/blob/main/LICENSE.TXT)
- [特定したWindows Release V1.4.0](https://github.com/rtlsdrblog/rtl-sdr-blog/releases/tag/V1.4.0)
- [V1.4.0のlibrtlsdrのライセンス表記](https://github.com/rtlsdrblog/rtl-sdr-blog/blob/V1.4.0/src/librtlsdr.c)
- [V1.4.0のrtl_tcpのライセンス表記](https://github.com/rtlsdrblog/rtl-sdr-blog/blob/V1.4.0/src/rtl_tcp.c)
- [FSF: 静的リンクと動的リンク](https://www.gnu.org/licenses/gpl-faq.en.html#GPLStaticVsDynamic)
- [FSF: プログラムの結合と別プログラム間通信](https://www.gnu.org/licenses/gpl-faq.en.html#MereAggregation)

配布時には上記の最新版ではなく、実際に発行したバージョンに付属する文書を使用します。
