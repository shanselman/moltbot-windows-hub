# Extensions hub UI proof

Captured from `feat/extensions-hub` at `eee53c31` on Windows ARM64 with an isolated app data directory. No skill or plugin mutation was performed while collecting proof.

- `skills-installed.png` shows the combined Extensions navigation, the agent selector, readiness filters, and the installed skill inventory returned by the connected Gateway.
- `skills-discover.png` shows a live ClawHub skill search. The connected Gateway omitted source-qualified install references for these results, so Review is safely disabled and the UI gives an upgrade explanation.
- `gateway-method-gated.png` shows the Plugins tab against a Gateway that does not advertise the required plugin lifecycle RPC methods. The page hides false inventory states and gives an explicit compatibility message.

The app remained responsive after switching tabs, loading the 54-skill inventory, and running the live search. The plugin install, capability-consent retry, policy-warning retry, and reconnect state machines are covered by `ExtensionsPageViewModelTests`; live plugin lifecycle proof requires a provenance-validated plugin-capable Gateway release.
