# Localisation

Every user-facing string lives in `strings.json`; the English text is the key's
source of truth and the Russian catalogue sits next to it. Two generated files
come out of it:

- `S.g.cs` — typed accessors (`S.ScanMyPc`), used from code and XAML.
- `Strings.resx` / `Strings.ru.resx` — the catalogues the app loads at runtime.

Regenerate both after editing `strings.json`:

```bash
node tools/strings.mjs
```

## Using a string

```csharp
// plain string
Title = S.CatRecycle;

// template with placeholders (order is whatever the translation needs)
Detail = Loc.Format("DownloadedAgo", Format.Ago(t));

// plurals: Russian gets one/few/many, English uses one/many
Detail = $"{count:N0} {Loc.Items(count)}";
```

```xml
<TextBlock Text="{x:Static loc:S.ScanMyPc}" />
```

## Adding a language

1. Add a `Strings.<culture>.resx` next to `Strings.resx` and translate the
   `<value>` elements — keys stay in English exactly as in `strings.json`.
2. Culture names follow .NET: `ru`, `de`, `pt-BR`, `zh-Hans`.
3. Nothing else: `Loc` picks the catalogue from the system UI language.

`PURGEWINDOWS_LANG=ru` overrides the detected language, which is handy for testing:

```bash
PURGEWINDOWS_LANG=ru dotnet run --project src/PurgeWindows -- --demo
```

## Plural rules

`Loc.Plural` implements the two Russian-family rules the app needs (one/few/many);
add a branch there for languages with other rules before shipping their
catalogue. English falls back to one/many.
