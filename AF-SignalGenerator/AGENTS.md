# AF-SignalGenerator 構成管理

- 親ソリューション `AF-SDR.sln` を使用する。別の `.sln` を作らない。
- 独立したWindows x64 WinForms EXE。AF-SDR製品プロジェクトへの参照・DLL依存を追加しない。
- バージョンは `AF-SignalGenerator.csproj` の `Version`。初回は1.0.0。AF-SDR本体とは別管理。
- 親のSemantic Versioning規約に従い、ソリューションルートのHISTORY.mdに製品名付きで記録してコミットする。
- 検証は親ソリューションのReleaseビルドとAF-SDR.Checks。信号モデルの変更時は再現性・RMS/SNR・同期検証を行い、SIGNAL-MODEL.mdとJSONのAlgorithmを整合させる。
