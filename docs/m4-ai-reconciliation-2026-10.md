> Kaynak: `scripts/m4-ai.ps1 reconcile --month 2026-10`, son üretim 2026-10-28; M4, HypothesisGeneration v2 pilotu ve M7 (FindingExtraction pilotu ve yeterliliği) dahil. Google faturası ay kapanınca kullanıcı tarafından girilir: `reconcile --month 2026-10 --invoice-try <tutar> --usd-try <kur>`. Fark %10'u aşarsa fiyat tablosu gözden geçirilir ve olay audit'e yazılır (ADR-0010).

# AI harcama mutabakatı — 2026-10

- Fiyat tablosu: `gemini-list-prices 2026-10-19 (sources.md S-G2, S-G9)` (liste fiyatı, USD)
- Ledger toplamı: **0.744680 USD** · çağrı 190 · girdi token 342910 · çıktı token 180043
- Fatura: girilmedi (mutabakat bekliyor)

| Görev sınıfı | Tutar |
|---|---|
| (none) | 0.015788 USD |
| FindingExtraction | 0.072868 USD |
| HypothesisGeneration | 0.656024 USD |

| İş | Tutar |
|---|---|
| fe-pilot-067181b18acc | 0.028655 USD |
| hg-pilot-0f2b043f7786 | 0.510653 USD |
| hg-pilot-270fe2bf4ac2 | 0.032664 USD |
| qualification-89075de83cd6 | 0.112708 USD |
| qualification-9e8bc4ad8c62 | 0.044213 USD |
| spike-20261018T215804Z | 0.000000 USD |
| spike-20261018T221822Z | 0.000856 USD |
| spike-20261019T145045Z | 0.007146 USD |
| spike-20261019T150445Z | 0.007786 USD |

| Gün | Tutar |
|---|---|
| 2026-10-18 | 0.095625 USD |
| 2026-10-19 | 0.576187 USD |
| 2026-10-28 | 0.072868 USD |
