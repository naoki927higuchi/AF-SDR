AF-SignalGenerator 1.0.0 / Windows x64 配布版
===========================================

【用途】
AF-SDR等で信号表示・周波数補正・Symbol Rate調整を検証するための、
既知条件のデジタル変調IQ WAVファイル生成器です。
無線機から電波を送信するアプリではありません。

【動作条件・起動】
Windows x64 / .NET 9 Windows Desktop Runtime (x64) が必要です。
https://dotnet.microsoft.com/ja-jp/download/dotnet/9.0
実行にSDK、AF-SDR.exe、RTL-SDRドングル、デバイスドライバーは不要です。
ZIPをすべて展開してAF-SignalGenerator.exeを起動してください。
EXEだけでなく、同じフォルダーのDLL・deps.json・runtimeconfig.jsonも必要です。
本配布はランタイムやドライバーをインストールしません。

【最初の信号を作る】
1. 起動し、Outputの「出力先…」から保存フォルダーを指定します。
2. 必要なら「初期値に戻す（Golden Signal）」を押して確認します。
3. Generateを押します。完了するとIQ WAVと同名のJSONが作成されます。
4. AF-SDR側でファイル入力を選び、「IQ WAVを開く…」からWAVを選び、再生します。
   デジタル信号表示はQPSK、9600 baud、RRC α=0.35で確認できます。

Golden Signalの初期条件:
QPSK / Fc 100 MHz / 9600 baud / RRC α 0.35 / 250 kS/s /
-20 dBFS / 10秒 / RandomBits / Seed 1。
Carrier/Clock Offset・Drift・Jitterは0、初期位相・Timingは0、AWGNとRandom指定はOFF。
WAVは約20 MBです。必要容量は概ねSample Rate × Duration × 8 bytesです。

【設定・操作】
対応方式: BPSK、QPSK、8PSK、16/64/256 QAM、π/4 Shift QPSK、
ASK/OOK（ASK 2/4/8値）、2/4/8 FSK、MSK、GMSK。
変調方式に応じて固有オプションが切り替わります。
CarrierとSymbol ClockのOffset・Drift・Gaussian Jitter、AWGN/SNRを設定できます。
Jitter相関スケールは揺らぎの時間尺度です。RMS値と合わせて指定します。
RRC αはPSK/QAMに使用します。ASK/OOKやGMSK等は方式固有の整形を使用します。

同じ設定・Seed・同じビルド／実行環境で同じ出力を再現できます。
「Random Seed」はSeedを更新します。「設定／付随JSONを開く…」で条件を復元できます。
Generateは既存WAV/JSONを上書きせず連番を付けます。生成中はキャンセルできます。
入力の組み合わせが不適切な場合はエラー内容を確認し、設定を変更してください。

【出力の扱い】
RF64/WAVE、2ch IEEE float32、ch1=I、ch2=Q、複素数I+jQです。
Fcは記録中心のメタデータであり、RF波を直接生成する値ではありません。
ファイル名の「100000000Hz」等はAF-SDRが中心周波数を取得するため、保持してください。
JSONは生成条件・正解値・測定値・WAVハッシュを記録します。
AF-SDRはWAVとファイル名を使います。JSONは条件の再現・照合用です。
Levelは複素信号全体のRMS、SNRはサンプリング全帯域での電力比です。
FFTの各binの高さや受信フィルター通過後のSNRとは同一ではありません。
詳しい数学的定義・各方式の制約はSIGNAL-MODEL.mdを参照してください。
同文書の「AF-SDR本体は1.10.0のまま」は生成器追加時の開発経緯です。
AF-SDRの専用解析に対応していない方式はI/Q表示等で観察してください。

【設定保存・削除】
%LOCALAPPDATA%\AF-SignalGenerator\settings.json に前回値を保存します。
起動だけでは生成を開始しません。「初期値に戻す」でも入出力フォルダーは保持します。
更新時は終了して新しい版を別フォルダーへ展開します。設定は同じユーザー内で共有します。
削除時は配布フォルダーを削除してください。設定と生成済みWAV/JSONは残ります。
個人設定、ユーザーの録音、生成済み信号は本ZIPに含めていません。

【ライセンスと内容】
LICENSE-NOTICE.md、COPYINGを参照してください。
Sourceにはアプリのソースとビルド手順を同梱しています。
外部NuGet・ネイティブDLLの依存はなく、.NETランタイム自体は同梱していません。
本体の機能・信号生成モデル・バージョンは1.0.0から変更していません。
SHA256SUMS.txtは同梱ファイルの整合性確認用です。
