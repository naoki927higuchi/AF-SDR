# Windowsアプリ・ゲームの配布ZIP運用

## AF-SDR

- 開発用ZIP: `dist/`（Git管理対象外）。通常のビルドはここへ保存する。
- リモート配布用ZIP: `Distribution/`。公開時に選定したZIPとSHA256だけを配置する。
- ここにある説明書やインストールスクリプトは梱包用原本であり、ZIPとは別の管理対象。

1. ローカルでビルドし、各製品の変換テスト・自己テスト・パッケージ検証を実行する。
2. pushする時点で対象ソースと対応するZIPを1つ選ぶ。公開済み版数の上書きは行わない。
3. `Prepare-Release.ps1 -Version <公開版数> -ZipPath <開発ZIP>` を実行する。
4. HISTORY.mdに公開日時・変更概要・採用したZIPを記録する。
5. 対象ソースと選定した公開ファイルだけをコミットしてpushする。スクリプト自体はpushしない。

Prepare-Release.ps1は元のチェックサムを照合してコピーする。各製品の動作・署名検証の代わりにはならない。
古いローカルZIPにSHA256がない場合は既存のパッケージ検証を先に行い、SHA256を作成してから選定する。
未公開の古いZIPを一括で追加せず、既存の公開ZIPと公開版数は保持する。
公開前の準備物を取りやめた場合は、コミット・pushの対象から外す。

この規則の対象は指定されたWindowsアプリ・ゲーム6件のみ。WebアプリOreComicは対象外。

## ブログからの最新バイナリー導線

- ブログ本文はGitHubのソース一式と `Distribution/README.md` を案内する。WordPressへ配布ZIPを新規アップロードしない。
- 新しい配布ZIPを公開するときは、このREADMEの製品別最新版・ZIPリンク・SHA256リンクも同じコミットで更新する。過去のZIPは同じ版数で上書きしない。
- Issues、Pull requests、Discussions、Wiki、Projects、Actionsは無効。外部の共同編集者を追加しない。
- 秘密情報スキャンとpush protection、Dependabot通知を有効化し、既定ブランチのforce push・削除を禁止する。
- コメント等のinteraction limitはGitHubの上限6か月で設定する。恒久設定ではないため、期限前に設定を再確認する。
