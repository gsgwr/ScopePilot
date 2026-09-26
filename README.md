# ScopePilot MVP

ScopePilotは、受託Web診断の事前探索と診断対象リクエストの整理を支援するWindowsアプリです。

## 現在の実装

- 案件条件、許可Origin、認証ロール、禁止操作、探索上限の保存
- 保存済み案件の一覧表示と切り替え（通信、候補、所見、判定記録を案件単位で復元）
- 案件ごとの参照ガイドライン選択と、候補選定・AIコンテキストへの連動
- HAR、JSONL、URL一覧TXTの取込
- URLパターン・HTTPメソッドによる代表化
- 同一Method・URLでも認証ロールが異なる通信を保持し、候補根拠とAI入力へ観測ロールを反映
- 今回探索する認証ロールの選択、認証情報を扱わない手動ログイン待機、同一ブラウザセッションでの再開
- CMS記事で使われる英字＋連番、日付スラッグ、数値ID、GUIDの同一テンプレート集約
- 画面確認済みでフォーム・入力点等の手掛かりがない単純GET HTMLを「静的画面候補」として除外候補にする優先度判定
- 候補選択時のURL正規化、メソッド、応答、画面確認状況、選定根拠、参照ガイドラインのシミュレーション表示
- 診断対象候補の代表URLをクリックして既定ブラウザで開く操作
- Windowsのライト・ダークアプリモードへ追従する画面配色とタイトルバー
- 採用候補ごとの「静的と断定できなかった理由」と診断実行時の注意事項
- 認証、権限、ファイル、外部連携、状態変更、API、AI機能、運用・クラウド機能などの初期分類
- Codex、Node.js、Playwright MCP、Burp MCPの環境チェック
- Codex探索ジョブの起動、実行ログ表示、停止
- Playwrightによる画面探索とBurp経由の通信観測
- 高速GETクローラによるサイト全体のリンク巡回
- `robots.txt`のSitemap指定と同一Originの`sitemap.xml`からのURL発見（既存の上限・禁止操作・許可Originを適用）
- HTTPメソッド、Content-Type、拡張子、URL、クエリ、フォーム、応答ステータス、認証ロールによるAI投入前のパターン分類
- 静的ページ・画像・CSS・JavaScript・フォント等のAI入力除外と、動的代表パターンへの集約
- AI確認対象がない場合のCodex探索自動省略
- 開始URLは静的画面候補に見えても初回のAI確認を省略しない保護
- MFA、CAPTCHA、SSOなどの手動操作待ちと同一ジョブでの再開
- 探索完了後の `observed-requests.jsonl` 自動取込
- Burp MCPの履歴取得が失敗した場合も、保存済みの観測を部分結果として取り込む
- 候補の採用・除外状態を案件に保存し、次回は採用済みと新規発見パターンをCodexへ渡す
- 案件単位で採用・除外・確認済みの保存記録を一括解除し、観測通信から候補を再判定
- 採用候補からBurp Suite Community Edition用のScope正規表現、代表URL、JSON証跡を出力
- Codexの探索結果から診断所見、深刻度、確信度、観測根拠、対象URL、制約を取り込み、案件へ保存
- 診断所見のJSON/HTMLレポート出力
- Codexに渡す `engagement.json`、`prompt.md`、`selection-guidance.md`、構造化出力スキーマの生成
- `%LOCALAPPDATA%\ScopePilot` への案件・実行パッケージ保存
- 案件ごとの探索実行履歴、結果概要・制約・通信数の一覧表示、実行成果物の参照と再試行
- 直前の探索実行と比較した新規・応答変更・今回未観測のリクエストパターン差分
- 探索コンソールの選択コピー、右クリックコピー、ログ全体コピー
- Playwright MCPとPortSwigger MCP stdioプロキシのCodex登録

高速クロールの全件ログは証跡として保存しますが、Codexには渡しません。ScopePilotが確認対象のリクエストとフォームをアプリ側で代表化し、最大200リクエストパターン・100フォームパターンの `ai-input.json` と短いガイドライン要約だけをCodexへ渡します。Codexは代表的な機能の確認と追加通信の収集を担当し、結果をScopePilotが候補へ変換します。リンク巡回に加えて、利用可能な`robots.txt`とサイトマップから同一OriginのURLを補完的に発見します。未確認HTMLを静的と断定せず、フォーム付き画面、認証・認可境界、API、エラー応答などを残します。選定は一次判定であり、脆弱性や安全性の判定ではありません。

選定基準の出典はWebAppPentestGuidelines、OWASP Top 10:2025、ASVS、WSTG、AISVS、Cloud Native Application Security Top 10、デジタル庁DS-221です。AISVSはAI機能、Cloud Native Top 10は該当構成、DS-221は適用対象の案件に限って参考にします。各出典へのリンクと適用条件は探索パッケージの `selection-guidance.md` に記載します。

## 操作手順

1. Burp Suite Community Editionを起動し、Proxy Listenerを `127.0.0.1:8080` で待ち受けます。
2. BurpのMCPタブでサーバーを有効にします。
3. ScopePilotの案件設定で開始URLと診断を許可されたOriginを入力します。
4. 初回だけ「MCPをセットアップ」を押し、Codexを再起動します。
5. 「AI探索を開始」を押します。
6. 手動操作待ちになった場合は、開いているブラウザで操作してから「手動操作完了」を押します。
7. 完了後、「発見した通信」と「診断対象候補」を確認します。
8. 採用候補をBurpへ反映する場合は「Burp Scope出力」を押し、生成された `burp-scope-regex.txt` をBurpのTarget > Scopeへ手動登録します。
9. Codexが所見を出力した場合は「診断所見」タブで根拠と制約を確認し、「所見レポート出力」でJSON/HTMLを保存します。
10. 過去の結果や失敗理由は「実行履歴」タブで確認します。選択した実行のログ、AI入力、Codex結果、探索サマリーを直接開けます。

高速クロールで `ERR_PROXY_CONNECTION_FAILED` が表示された場合は、開始URLへ到達できていません。BurpのProxy settingsで `127.0.0.1:8080` のListenerを有効にし、ScopePilotの「環境チェック」でBurp ProxyがOKになってから再実行してください。通信をBurpで観測できない状態では、Codex探索へ進めず失敗として停止します。

別案件へ切り替える場合は「新しい案件を開始」を押します。現在の案件を保存したうえで、案件設定、観測通信、診断対象候補、手動操作表示、実行ログを新しい案件用に初期化します。保存済みの旧案件ファイルは削除しません。

## ビルドと起動

```powershell
$env:DOTNET_CLI_HOME = "$PWD\.dotnet-home"
Push-Location tools/playwright-runtime
npm ci
Pop-Location
dotnet build ScopePilot.csproj
dotnet run
```

GitHub Actionsでも同じ手順でPlaywright MCPランタイムを復元し、win-x64の自己完結型配布物を成果物として生成します。

## MCPの前提

- Playwright MCP本体はアプリに同梱され、インストール済みNode.jsから直接起動します。
- Burp: BApp StoreのMCP ServerをBurp Community Editionに追加し、MCPタブから有効化
- ScopePilotのセットアップにより、PlaywrightブラウザはBurp Proxy経由に設定されます。

外部サイトへの探索は、案件の許可範囲と禁止操作を確認してから実行してください。
