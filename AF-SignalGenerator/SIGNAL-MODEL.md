# AF-SignalGenerator 1.0.0 信号モデルと出力仕様

## 構成・起動

既存 `AF-SDR.sln` にAF-SignalGeneratorプロジェクトを追加。製品間のプロジェクト参照はなく、AF-SDR本体は1.10.0のまま。検証プロジェクトだけが両製品を参照する。

```powershell
dotnet build AF-SDR.sln -c Release
.\AF-SignalGenerator\bin\Release\net9.0-windows\AF-SignalGenerator.exe
dotnet run --project AF-SDR.Checks -c Release --no-build
```

Windows x64 / .NET 9 Desktop Runtime。外部NuGet・SDRドライバ・AF-SDR.exeは生成器の起動に不要。配置時はEXEだけでなく同じ出力フォルダのDLL・deps.json・runtimeconfig.jsonも含める。

## 操作

1. Signalで方式・公称値・データ系列・Seedを設定。方式固有欄はQAM/FSK/GMSK/ASKに応じて切り替わる。RRC αはPSK/QAMだけで使用。
2. 必要なCarrier/Clock誤差、AWGNをON。Jitter相関スケールは滑らかな揺らぎの時間尺度で、受信フィルターの帯域ではない。
3. Outputへ保存先フォルダを指定しGenerate。前半で実際の信号電力を測定、後半でWAVとJSONを出力。キャンセル可能。
4. 同名ファイルがあれば連番を付け、既存ファイルを上書きしない。生成中は一時ファイルを使用し、不完全なファイルを完成品として残さない。
5. 「初期値に戻す」は確認後に全信号パラメータをGoldenへ戻す。入出力フォルダは保持する。

設定は `%LOCALAPPDATA%\AF-SignalGenerator\settings.json` に変更後約700 ms、生成時、終了時に保存。入出力フォルダ、非表示の方式固有値、Random指定、Seedも対象。「設定／付随JSONを開く…」で保存設定または生成したJSONのParametersを読み込み、以前の条件を再現できる。起動しただけでは生成しない。

GoldenはQPSK、Fc=100 MHz、9600 baud、α=.35、250 kS/s、-20 dBFS、10秒、RandomBits、Seed=1。Carrier/Clock誤差・ドリフト・ジッターは全て0、位相0度・Timing 0、Random指定OFF、AWGN OFF。意味を持たない方式固有値も初期値へ戻る。

## IQ WAV・JSON

- RF64/WAVE、2ch IEEE float32 LE。ch1=I、ch2=Q、複素数はI+jQ。Fs=250000～3200000 S/s。8-bitへ再量子化しない。
- ds64（28 bytes）・fmt（18 bytes、tag=3）・fact（4 bytes）・data。データ開始94 bytes。1ペア8 bytes。ds64に64-bit長とフレーム数を記録。factはUINT32に収まらない場合0xFFFFFFFF。
- N=round(Duration*Fs, AwayFromZero)。実際の記録時間はN/Fs。ファイル全体をメモリに保持しない。
- ファイル名 `AFSG_QPSK_100000000Hz_seed1.wav` 等のHzトークンからAF-SDRが記録中心Fcを取得する。Fcはベースバンドの周波数原点を表すメタデータであり、100 MHz正弦波を250 kS/sで作る意味ではない。
- 同名`.json`はUTF-8、Schema=`AF-SignalGenerator.IqTestVector`、SchemaVersion=1、Algorithm=`AFSG-1.0.0`。現AF-SDRには付随JSONの読込規約がないため、JSONは正解値・再生成用の独立した補助仕様。AF-SDRはWAVとファイル名だけで受信する。
- Parametersは全入力値、Waveはレート・中心・ペア数・形式・WAVのSHA-256、Resolvedは実現したRandom位相/Timing・正規化ゲイン、Modelsは数学的定義、Measurementsは実測値。Truthは最大約1001点のサンプル番号・時刻・周波数偏差・実Baud・シンボル座標・ジッター・搬送波位相。端点を含むが全サンプルの波形ログではない。詳細再現はParameters+Algorithm+Seedを使用。
- 日時・絶対保存先を信号やJSONに埋め込まない。同一設定・Seed・同一ビルド/実行環境ではWAVとJSONがバイト単位で一致する。異なるCPU/.NET版にまたがる数学関数の最下位ビット一致は保証対象外。

## データ系列・写像

乱数生成は固定SplitMix64（加算0x9E3779B97F4A7C15、xor/乗算0xBF58476D1CE4E5B9、0x94D049BB133111EB）。データ、Carrierジッター、Baudジッター、Random位相、Random Timing、AWGNに独立したseed XORドメインを使用。ある劣化をONにしても送信データ乱数の消費順は変わらない。GaussianはBox–Muller法。

RandomBitsは64-bit出力のLSBから消費し、1シンボルのコードは消費順にMSBから構成。PRBS15はx^15+x^14+1、PRBS23はx^23+x^18+1。左シフト、MSB出力、該当次数ビット同士のxorをLSBへ入力。Seedの下位15/23 bitを状態とし、全0は1に置換。この置換は系列の定義でありSeedの表示値は変更しない。

| 方式 | 基底信号 |
|---|---|
| BPSK | 0→+1、1→−1。RRC |
| QPSK | 00→(+,+)、01→(+,−)、10→(−,+)、11→(−,−)、各軸1/√2。RRC |
| 8PSK | 3bit Gray逆変換の整数k→exp(jπk/4)。RRC |
| QAM | 16/64/256。I/Q各軸をGray逆変換して等間隔の奇数振幅へ写像。平均シンボル電力を1へ正規化。RRC |
| π/4 Shift QPSK | 00/01/10/11→差動位相+π/4,+3π/4,−π/4,−3π/4。位相累積後RRC |
| OOK | 0/1。GaussianでNRZ包絡線を平滑化、BT=.5 |
| ASK | 2/4/8値。振幅(code+1)/M、ゼロ振幅を含まない。Gaussian NRZ、BT=.5 |
| FSK | 2/4/8トーン、(code−(M−1)/2)×Spacing Hz。矩形周波数パルスを積分した連続位相FSK。間隔はBaud誤差と独立 |
| MSK | NRZ ±1×実Baud/4を積分。h=.5。シンボルごとの位相リセットなし |
| GMSK | Gaussian平滑化したNRZ ±1×実Baud/4を積分。h=.5、BT=.1～1 |

RRCは解析式を±12シンボルで打ち切り、1シンボル2048分割のテーブルを線形補間。α=0と式の特異点には極限値を使用。Gaussian NRZは
`p(u)=0.5*[erf(a*(u+0.5))-erf(a*(u-0.5))]`, `a=2πBT/sqrt(2 ln 2)`。
同じ±12シンボル支持範囲・補間を使用。erfは最大絶対誤差約1.5e-7の多項式近似。Gaussianで平滑化したASKにはRRCを二重適用しない。

生成開始はシンボル座標24+Timing Offset。前後の疑似ランダムシンボルを準備してパルス整形の開始過渡を避ける。ファイル冒頭をゼロでパディングしない。連続位相方式の変調位相はt=0で0。

RRCとGaussianの定義参考: [MathWorks RRC filtering](https://www.mathworks.com/help/comm/ug/raised-cosine-filtering.html)、[MathWorks GMSK Gaussian pulse](https://au.mathworks.com/help/comm/ref/comm.gmskdemodulator-system-object.html)。上記の打切り長・補間・正規化はこの実装の値。

FSK/MSKの矩形周波数パルスは、サンプル区間内のシンボル境界を分割して積分する。MSKはシンボル座標の面積から±π/2/symbolを保つ。FSKの境界時刻は区間内の線形Baud補間から計算する。矩形パルスへ端点台形則だけを適用して発生するデータ依存の位相誤差を避ける。滑らかなGMSK変調周波数はFs刻みの台形則を使用。

## Carrier / Symbol Clockの誤差

時刻t=n/Fs、公称BaudをBとする。

```text
f(t) = FrequencyOffset + FrequencyDrift*t + FrequencyJitter*j_c(t)  [Hz]
b(t) = B * (1 + 1e-6*(BaudOffsetPpm + BaudDriftPpmPerSecond*t + BaudJitterPpm*j_b(t)))
φ(t) = φ0 + 2π ∫ f(t)dt
u(t) = 24 + TimingOffset + ∫ b(t)dt
z(t) = shaped_signal(u(t)) * exp(jφ(t))
```

CarrierのφとClockのuは各サンプル間の台形則で積分。uには補償加算を使い、長時間の丸めによる人工的なクロックドリフトを抑える。固定offsetと線形driftでは積分は解析値と一致し、jitterではFs刻みの数値積分になる。正のCarrier OffsetはAF-SDRの右側・高周波数側。正のBaud Offsetは速いクロック。Timing Offsetは0～1 symbol、正で座標を進める。Random Phaseは[0,360)度、Random Timingは[0,1) symbolを独立したseed系列から一度だけ選ぶ。

### Jitterモデル

Carrier/Clockそれぞれに独立な標準Gaussianノットg_kを生成。ノット間隔TはUIのJitter相関スケール（初期100 ms）、ノット原点にはSeed由来のランダムオフセットを使用する。区間内の位置r∈[0,1]について

```text
h = (1-cos(πr))/2
j(t) = ((1-h)*g_k + h*g_(k+1)) / sqrt((1-h)^2+h^2)
```

各時刻でGaussian・母平均0・母RMS1であり、ノット境界でも値と一次微分が連続する。指定RMSを掛けてHzまたはppmへ変換。毎サンプル独立の値を直接与えず、ランダムな周波数ホッピングもしない。Tは−3 dB帯域の数値ではない。

有限の録音区間では平均が厳密な0、RMSが指定値に厳密一致するとは限らない。短区間の実現値を再正規化すると確率過程の意味が変わるため、平均除去・区間RMS合わせをせず実測mean/RMSをJSONに残す。特にDurationがTより短い場合、この点に注意する。

## レベル・AWGN

0 dBFSは単位複素RMS。`P=mean(I_clean²+Q_clean²)`、`Level=10log10(P)`。2回の決定論的生成を行い、1回目の実電力を使って2回目の信号電力を指定レベルへ一致させる。ピーク値やFFT 1 binの値ではない。AF-SDRのスペクトラムはdBFS/binなので、広帯域信号の各binが指定レベルと一致するわけではない。

AWGNは独立なI/Q Gaussian、各軸分散 `P/(2*10^(SNR/10))`。Fsの全帯域について信号電力/雑音電力を定義し、Eb/N0・受信フィルター後SNRとは区別する。有限長で実測SNRは揺らぐためJSONに実測値も保存。出力の±1超を勝手にクリップしない。float出力に保持し、超過フレーム数・ピークを記録しUIにも通知する。

## 検証・限界

- Sample Rate、Baud、方式の帯域、全Durationのdrift、jitterの6σ余裕を組み合わせて生成前に検証。実Baudには最低8 samples/symbol、信号の帯域見積もりとCarrier偏差には±0.45 Fsの余裕を要求する。
- FSK/MSK/ASKの帯域見積もりは主要帯域の保守的評価。有限長RRC、矩形周波数パルス、Gaussian補間は数学的な厳密帯域制限ではない。
- Gaussianは非有界なので6σは絶対保証ではない。1回目の全サンプル走査で実際のBaudと帯域条件も検査し、超えれば完成ファイルを出さずエラーにする。
- Golden QPSKはAF-SDRの実際のコンスタレーションDSPでEVM約1.24%、CFO約−0.013 Hz、実電力−20.000000 dBFSを確認。全方式・指定多値数のWAVをAF-SDRのIqWaveReaderで読み取り、有限値・長さ・形式を確認。
- 同一Seed/全劣化ONの再現性、GaussianのRMS/平均/尖度/連続性、実波形の周波数ドリフト、積分シンボル座標、SNR、PRBS15周期、MSK偏移、設定復元、Golden復帰、キャンセルを検証。既存AF-SDRの回帰テストも維持。
- 現行AF-SDRの8PSK/256QAM/GMSK専用同期や8FSK専用表示は未実装。生成ファイルは読み込めるが、これらを専用同期表示できるようAF-SDRを拡張したものではない。既存I/Q表示やスペクトラム、将来の対応実装のテストに使用する。
- マルチパス、フェージング、FEC、BER、送信ハードウェア制御は含まない。
