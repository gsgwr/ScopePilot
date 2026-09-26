# ScopePilot 0.11.0 配布版

このZIPはWindows x64向けの自己完結型ポータブル版です。.NETランタイムの別途インストールは不要です。

## 初回起動

1. ZIPをローカルフォルダーへ展開します。ネットワーク共有上やZIP内から直接実行しないでください。
2. Node.js 24以降、Burp Suite Community Edition、Codex CLIを用意します。
3. Burp SuiteでProxy Listener `127.0.0.1:8080` とMCP Serverを有効にします。
4. `ScopePilot.exe` を起動し、「環境チェック」と「MCPをセットアップ」を実行します。
5. Codexを再起動してから探索を開始します。

Playwright MCPランタイムとBurp MCP用stdioプロキシは配布物へ同梱されます。案件データは `%LOCALAPPDATA%\ScopePilot` に保存されます。

## 配布物の検証

ZIPと同じ場所にある `.sha256` ファイルと、PowerShellの `Get-FileHash <zip> -Algorithm SHA256` の値が一致することを確認できます。

コード署名はまだ適用していません。組織内配布で署名が必要な場合は、署名証明書を用意した後に署名工程を追加してください。
