# AF-SDR 構成管理ルール

- バージョン表記はSemantic Versioningの `メジャー.マイナー.パッチ`。初回ビルド成果物は `1.0.0`。
- 上位の番号を増やすときは下位の番号を0に戻す。
- メジャー更新は互換性の破棄を伴う変更。ユーザーの指示または定義された明確な設計変更に基づく場合のみ行う。
- マイナー更新は互換性を保つ機能追加。開発者の判断で行う。
- パッチ更新は機能追加にあたらない不具合修正・軽微なリファクタリング・調整。開発者の判断で行う。
- ソリューションルートの `HISTORY.md` に、バージョン番号・日時・変更概要を都度記録する。
- バージョンをインクリメントして `HISTORY.md` を更新するタイミングで、バージョン更新用のGitコミットを作成する。
- 製品バージョンの設定元は `AF-SDR/AF-SDR.csproj` の `Version`。成果物と履歴のバージョンを一致させる。
- .NETのAssemblyVersion/FileVersionは4桁の内部メタデータであり、利用者向けバージョンには3桁のVersion/InformationalVersionを使用する。
- 検証は `dotnet build AF-SDR.sln -c Release` と `dotnet run --project AF-SDR.Checks -c Release --no-build`。後者はハードウェアを開かない。
- AF-SDR 1.11.0以降のReleaseは `AF-SDR/bin/Release-<Version>/` へ直接出力する。旧 `Release/net9.0-windows` は更新しない。回答のEXEリンクはバージョン付きフォルダーを指すこと。

## AF-SignalGenerator

- 同じソリューション内の独立した製品。バージョン設定元は `AF-SignalGenerator/AF-SignalGenerator.csproj` の `Version` とし、初回1.0.0。AF-SDR本体の版数と分離する。
- HISTORY.mdには製品名を付けて更新を記録する。既存AF-SDRプロジェクトの版数・動作を理由なく変更しない。
