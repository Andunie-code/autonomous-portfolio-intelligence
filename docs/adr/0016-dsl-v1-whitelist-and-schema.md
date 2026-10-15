# ADR-0016: DSL v1 — beyaz liste ve şema

- Durum: Kabul edildi (Kapı A, kullanıcı onayı) · Tarih: 2026-10-16
- Karar türü: kapsam **U** (indikatör havuzu ve rejim filtresi, 2026-10-16); şema ve sınırlar **T**
- Uygulama durumu: **Planlandı, uygulanmadı**
- İlgili: ADR-0003 (DSL ilkesi), ADR-0014 (çıkışlar ve stop), ADR-0015 (aile), `m3-protocol.md` §1–§3, §8; `sources.md` S-G7

## Bağlam
ADR-0003 stratejilerin beyaz listeli, sığ şemalı bir JSON DSL ile tanımlanmasını öngörür ama beyaz listeyi belirtmez. M2 sonuçları düz trend stratejilerinin ayı ve yatay piyasada tek başına yetersiz kaldığını gösterdi. M4'te Researcher'ın filtreli stratejiler kurabilmesi için kullanıcı indikatör havuzunu genişletti (U, 2026-10-16).

## Karar

### İndikatör beyaz listesi
Formüller ve `L` değerleri `m3-protocol.md` §1'dedir.

| `type` | Parametreler (aralık) | Çıktılar |
|---|---|---|
| `sma` | `period` 2–300 | `value` |
| `ema` | `period` 2–300 | `value` |
| `rsi` | `period` 2–100 | `value` |
| `atr` | `period` 2–100 | `value` |
| `roc` | `period` 1–300 | `value` |
| `volume_sma` | `period` 2–300 | `value` |
| `macd` | `fast` 2–100, `slow` 3–300 (`fast < slow`), `signal` 2–100 | `line`, `signal`, `hist` |
| `adx` | `period` 2–100 | `adx`, `plus_di`, `minus_di` |
| `bollinger` | `period` 2–300, `k` 0,5–4 | `middle`, `upper`, `lower`, `bandwidth`, `percent_b` |
| `donchian` | `period` 2–300 | `upper`, `lower`, `middle` |
| `supertrend` | `period` 2–100, `multiplier` 0,5–10 | `upper`, `lower`, `dir` |

Her indikatör `source: "self" | "btc"` alır (varsayılan `self`; rejim filtresi, `m3-protocol.md` §2).

### Şema
```json
{
  "dsl": "1",
  "name": "ema-trend",
  "interval": "4h",
  "indicators": [
    { "id": "fast", "type": "ema", "params": { "period": 50 } },
    { "id": "slow", "type": "ema", "params": { "period": 100 } },
    { "id": "atr", "type": "atr", "params": { "period": 14 } }
  ],
  "entry": { "op": "cross_above", "args": [ { "ref": "fast.value" }, { "ref": "slow.value" } ] },
  "exit":  { "op": "cross_below", "args": [ { "ref": "fast.value" }, { "ref": "slow.value" } ] },
  "stop":  { "type": "atr", "ref": "atr.value", "multiple": 3, "trailing": false }
}
```
- **Koşul düğümü:** `{ "op", "args" }`. Karşılaştırmalar (`gt`, `gte`, `lt`, `lte`, `cross_above`, `cross_below`) tam 2 operand alır. `and`/`or` 2–4 koşul, `not` 1 koşul alır.
- **Operand:** `{ "ref": "<id>.<çıktı>", "lag": 0|1, "mult": x }`, `{ "price": "open|high|low|close|volume", "source": "self|btc", "lag": 0|1, "mult": x }` ya da `{ "const": x }`. `mult` ∈ (0, 100], varsayılan 1.
- **Stop:** `{ "type": "percent", "pct": 0,01–0,10 }`, `{ "type": "atr", "ref": "<atr id>.value", "multiple": 0,5–10, "trailing": false }` ya da `{ "type": "indicator", "ref": "<id>.<çıktı>", "trailing": bool }`. `stop` yoksa yalnızca D25 hard stop'u geçerlidir. Stop referansı `self` kaynaklı olmalıdır.
- **Sınırlar:** en fazla 8 indikatör; koşul derinliği en fazla 3; `lag` en fazla 1; `interval` v1'de yalnızca `4h`; `name` 1–64 karakter, `[a-z0-9-]`.
- **Doğrulama:** bilinmeyen alan, tip, `op` ya da çıktı reddedilir; referanslar tanımlı indikatörlere çözülmelidir; parametreler aralık içinde olmalıdır; `id`'ler tekil ve `[a-z][a-z0-9_]{0,15}` biçiminde olmalıdır; yalnızca sabitlerden oluşan karşılaştırmalar reddedilir.
- Şema tek seviyeli düğüm tipleriyle sığ tutulur; JSON Schema dışa aktarımı Gemini'nin desteklediği alt kümeyle (S-G7) M4'te ayrıca doğrulanır.

### Ek: baseline'ların DSL karşılıkları
- **`ema-trend` v1:** yukarıdaki örnek.
- **`supertrend` v1:**
```json
{
  "dsl": "1", "name": "supertrend", "interval": "4h",
  "indicators": [ { "id": "st", "type": "supertrend", "params": { "period": 10, "multiplier": 3 } } ],
  "entry": { "op": "cross_above", "args": [ { "ref": "st.dir" }, { "const": 0 } ] },
  "exit":  { "op": "cross_below", "args": [ { "ref": "st.dir" }, { "const": 0 } ] },
  "stop":  { "type": "indicator", "ref": "st.lower", "trailing": true }
}
```

## Sonuçlar
- Yeni indikatör ya da operatör eklemek, ADR revizyonu ve insan incelemesi gerektiren bir kod değişikliği olmaya devam eder (ADR-0003).
- Rejim filtresi yalnızca `BTCUSDT`'ye bağlanır; başka çapraz varlık kaynakları v1'de yoktur.
- Kleene mantığı ve `Unknown` semantiği sayesinde eksik BTC verisi ya da ısınma hiçbir zaman giriş açtırmaz.
- DSL baseline'ları C# baseline'larıyla birebir parity testine tabidir; C# baseline'lar referans olarak kalır.
