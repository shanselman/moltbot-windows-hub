# Extensions hub UI proof

The app-window captures were recorded from `feat/extensions-hub` at `eee53c31` on Windows ARM64 with an isolated app data directory. No skill or plugin mutation was performed while collecting proof.

- `skills-installed.png` shows the combined Extensions navigation, the agent selector, readiness filters, and the installed skill inventory returned by the connected Gateway.
- `skills-discover.png` shows a live ClawHub skill search. The connected Gateway omitted source-qualified install references for these results, so Review is safely disabled and the UI gives an upgrade explanation.
- `gateway-method-gated.png` shows the Plugins tab against a Gateway that does not advertise the required plugin lifecycle RPC methods. The page hides false inventory states and gives an explicit compatibility message.

The app remained responsive after switching tabs, loading the 54-skill inventory, and running the live search. The plugin install, capability-consent retry, policy-warning retry, and reconnect state machines are covered by `ExtensionsPageViewModelTests`; live plugin lifecycle proof requires a provenance-validated plugin-capable Gateway release.

The reviewed UI fixes were revalidated from their exact production commit, `e049ee5b`, using the same isolated Windows ARM64 app state. UI Automation confirmed the Extensions page, Skills and Plugins tabs, Agent selector, installed inventory, ClawHub links, and the single older-Gateway compatibility message. The app remained responsive after live tab selection. Automated proof at that commit also passed:

- Full build on Windows ARM64.
- Shared: 3,958 passed, 32 environment-gated skips.
- Tray: 2,897 passed.
- Extensions page and view-model contracts: 31 passed.
- Native Axe scan for `ExtensionsPage`: 1 passed.
- Connection-epoch and Gateway extension contracts: 14 passed, including forced reconnects between consent validation and final transport write for both install and enable. In both cases the operation failed closed before the test server received a request.

The installed Gateway does not advertise plugin lifecycle RPCs, so allowed live mutations and server-side revoked-authority behavior remain not verified. Those require the `windows-wsl-gateway-e2e` proof pool with a provenance-validated plugin-capable Gateway.
