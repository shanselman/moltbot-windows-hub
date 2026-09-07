# Localization Guide

OpenClaw Tray uses WinUI `.resw` resource files for localization. Windows automatically selects the correct language based on the OS locale - no user configuration needed.

## Currently Supported Languages

| Language | Locale | Resource File |
|----------|--------|---------------|
| English (US) | `en-us` | `Strings/en-us/Resources.resw` |
| French (France) | `fr-fr` | `Strings/fr-fr/Resources.resw` |
| Dutch (Netherlands) | `nl-nl` | `Strings/nl-nl/Resources.resw` |
| Brazilian Portuguese | `pt-br` | `Strings/pt-br/Resources.resw` |
| Chinese (Simplified) | `zh-cn` | `Strings/zh-cn/Resources.resw` |
| Chinese (Traditional) | `zh-tw` | `Strings/zh-tw/Resources.resw` |

## Adding a New Language

1. **Copy the English resource file** as your starting point:

   ```
   src/OpenClaw.Tray.WinUI/Strings/en-us/Resources.resw
   ```

2. **Create a new folder** for your locale under `Strings/`:

   ```
   src/OpenClaw.Tray.WinUI/Strings/<locale>/Resources.resw
   ```

   Use the standard BCP-47 locale tag in lowercase (e.g., `de-de`, `fr-fr`, `ja-jp`, `ko-kr`, `pt-br`, `es-es`).

3. **Translate the `<value>` elements** - do not change the `name` attributes. Each entry looks like:

   ```xml
   <data name="SettingsSaveButton.Content" xml:space="preserve">
     <value>Save</value>   <!-- ← translate this -->
   </data>
   ```

4. **Keep format placeholders intact.** Some strings use `{0}`, `{1}`, etc. These must remain in the translation:

   ```xml
   <data name="Menu_SessionsFormat" xml:space="preserve">
     <value>Sessions ({0})</value>  <!-- {0} = session count -->
   </data>
   ```

5. **Do not translate resource key names** (the `name` attribute). Only translate `<value>` content.

6. **Add the locale to the `OPENCLAW_LANGUAGE` whitelist** in `src/OpenClaw.Tray.WinUI/App.xaml.cs`. The build system automatically discovers the new resource folder, but the development language override accepts only explicitly supported locales.

7. **Submit a pull request** with the new `Resources.resw`, whitelist, documentation, and locale validation updates.

## How It Works

### XAML strings (automatic)
Elements with `x:Uid` attributes are automatically matched to resource keys:
```xml
<Button x:Uid="SettingsSaveButton" Content="Save" />
```
Maps to resource key `SettingsSaveButton.Content`.

### C# runtime strings (via LocalizationHelper)
Code uses `LocalizationHelper.GetString("key")` to load strings at runtime:
```csharp
Title = LocalizationHelper.GetString("WindowTitle_Settings");
```

### Language selection
Windows picks the language automatically based on the user's OS display language. No in-app language picker is needed.

## Testing a Language Locally

Set the `OPENCLAW_LANGUAGE` environment variable before launching the app:

```powershell
$env:OPENCLAW_LANGUAGE = "fr-fr"  # or nl-nl, pt-br, zh-cn, zh-tw
.\run-app-local.ps1 -NoBuild
```

This overrides `LocalizationHelper.GetString()` calls for menus, toasts, dialogs, and the onboarding wizard. The language is validated against the supported locale list.

> **Note:** XAML `x:Uid` bindings follow the OS display language. For full localization testing including XAML elements, change your Windows display language in Settings → Time & Language.

## Resource Key Naming Conventions

| Pattern | Used For | Example |
|---------|----------|---------|
| `ComponentName.Property` | XAML `x:Uid` bindings | `SettingsSaveButton.Content` |
| `WindowTitle_Name` | Window title bars | `WindowTitle_Settings` |
| `Toast_Name` | Toast notification text | `Toast_NodePaired` |
| `Menu_Name` | Tray menu items | `Menu_Settings` |
| `Status_Name` | Status display text | `Status_Connected` |
| `TimeAgo_Format` | Relative time strings | `TimeAgo_MinutesFormat` |

### Onboarding Key Namespace

All onboarding wizard strings use the `Onboarding_` prefix:

| Pattern | Used For | Example |
|---------|----------|---------|
| `Onboarding_PageName_Label` | Page titles, descriptions | `Onboarding_Welcome_Title` |
| `Onboarding_Connection_*` | Connection page labels/status | `Onboarding_Connection_TestConnection` |
| `Onboarding_Perm_*` | Permission names | `Onboarding_Perm_Camera` |
| `Onboarding_Ready_*` | Ready page elements | `Onboarding_Ready_Feature_Voice_Subtitle` |
| `Onboarding_Wizard_*` | Wizard page elements | `Onboarding_Wizard_Continue` |

## Validation

All resource files must have the **same set of keys**. Locale directories are discovered dynamically under `Strings/`, so adding a new `Strings/<locale>/Resources.resw` file automatically brings it under validation. You can verify counts with:

```powershell
$base = "src\OpenClaw.Tray.WinUI\Strings"
Get-ChildItem $base -Directory | ForEach-Object {
    $loc = $_.Name
    $count = (Select-String -Path "$base\$loc\Resources.resw" -Pattern '<data name="' | Measure-Object).Count
    Write-Host "$loc : $count keys"
}
```

All locale counts should match. Missing or extra keys indicate an incomplete translation.

Non-English resource values must also follow the all-or-none rule enforced by `LocalizationValidationTests`: each key is either translated in every non-English locale, intentionally invariant in every non-English locale, or explicitly deferred with rationale. Partial translation, where only some non-English locales differ from `en-us`, is treated as a regression.
