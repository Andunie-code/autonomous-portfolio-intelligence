# M4 teknik spike sonuçları (Kapı C)

- **Durum:** Geçti. 5 senaryonun 5'i başarılı. Bu bir teknik spike'tır (testing §6.2), **yeterlilik değildir**; hiçbir görev sınıfına nitelik vermez.
- **Koşu:** kullanıcının etkileşimli koşusu, 2026-10-18T22:18:22Z (iş `spike-20261018T221822Z`). Komut `scripts/m4-ai.ps1 spike` (ağ bayrağı ve etkileşimli onayla), engine `e8945d8`.
- **Model:** `gemini-3.5-flash-lite` (D12; S-G1, S-G2). Yanıtların `modelVersion` alanı, yapılandırılmış adla birebir aynı: `gemini-3.5-flash-lite`.
- **Ortam:** ücretli katman (B1, 2026-10-19), Kapı B'deki `AiCallGuard`, ledger, kill switch ve tavanlarla.
- **İçerik:** yalnızca sentetik.
- **Maliyet:** gerçek **0,0008562 USD**; rezerve edilen tahmin 0,018276 USD. Spike tavanı 0,50 USD, aylık tavan 10 USD.

## Sonuçlar

| Senaryo | Sonuç | Çağrı | Token (girdi/çıktı) | Tahmin (USD) | Gerçek (USD) | Gecikme | Gözlem |
|---|---|---|---|---|---|---|---|
| structured-output | PASS | 1 | 31 / 19 | 0,002594 | 0,000057 | 1967 ms | Şema geçerli; `finishReason=STOP` |
| function-calling | PASS | 2 | 217 / 25 | 0,005207 | 0,000128 | 961 ms | `lookup_constant({"name":"alpha"})`, izin listesinde; sonuç "The value for alpha is 42." |
| function-calling + structured-output | PASS | 2 | 217 / 23 | 0,005229 | 0,000123 | 1162 ms | Aynı tool çağrısı; son yanıt şemaya uygun `{"value": 42}` |
| dsl-in-neutral-schema | PASS | 1 | 198 / 172 | 0,002648 | 0,000489 | 916 ms | `definition` alanında geçerli DSL v1; tanım hash'i `9471392f40ee…` |
| schema-enforcement | PASS | 1 | 41 / 19 | 0,002599 | 0,000060 | 590 ms | "Biçimi yok say" talimatına rağmen yanıt şemada kaldı |

## Çıkarımlar (T)
1. **Structured output ile function calling birlikte**, `gemini-3.5-flash-lite` üzerinde çalışıyor. ADR-0007'deki risk (S-G7: belgede yalnızca "Gemini 3 serisi" deniyordu) bu model için kapandı. Özellik belgede hâlâ önizleme olarak geçtiği için davranış değişebilir; yeterlilik koşusu tool'suz ve yalnızca structured output ile tasarlanır (`m4-hg-qualification-design.md`).
2. **DSL nötr şemaya sığıyor:** DSL v1 tanımı, şemada bir `string` alanı olarak taşınıyor ve `DslParser` ile doğrulanıyor. Ağaç yapısı şemada ifade edilmediği için şema sığ kalıyor (ADR-0016).
3. **`modelVersion` eşleşmesi:** `acceptedModelVersions: ["gemini-3.5-flash-lite"]` doğru; degraded işareti gereksiz yere tetiklenmiyor.
4. **Thinking:** varsayılan seviye minimal; çıktı token'ları 19–172 aralığında kaldı ve `maxOutputTokens` sınırına yaklaşılmadı (S-G8).
5. **Maliyet tahmini:** rezervasyon gerçek maliyetin 20–45 katı. Bunun nedeni, `maxOutputTokens`'ın tamamının rezerve edilmesi; bu kasıtlı ve muhafazakârdır. "Gerçek maliyet > tahminin 2 katı" tetiği (ADR-0010 c) bu profilde fiilen tetiklenmez; fiyat ya da token sayımında büyük bir hata olursa yine yakalar.
6. **Timeout, retry, eşzamanlılık (requirements §8 TBD):** gecikmeler 0,6–2,0 sn. Başlangıç değerleri (timeout 120 sn, en fazla 2 retry, eşzamanlılık 1) **değiştirilmedi**: aylık tavana göre yeterince muhafazakârlar ve spike bir hata göstermedi. Ölçüm, yeterlilik koşusunda sürer.
7. **Refusal:** spike'ta refusal gözlenmedi. "Biçimi yok say" talimatı şema dışı bir çıktı üretmedi. Router'ın refusal yolu (`NoDecision` → `onUnavailable`) çevrimdışı testlerle doğrulanmıştır.

## Önceki deneme (kayıt)
Aynı gün daha önce yapılan ilk spike'ta 5 senaryonun 5'i, sağlayıcı tarafında HTTP 400 `INVALID_ARGUMENT` ile başarısız oldu (maliyet 0 USD). Hata mesajı o sürümde rapora yazılmıyordu, bu yüzden kök neden kesin olarak bilinmiyor. Raporlama düzeltildi (`e8945d8`; CHANGELOG 2026-10-19). Sonraki koşu bu belgedeki sonuçtur.
