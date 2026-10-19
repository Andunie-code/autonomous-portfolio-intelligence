> Kaynak: `scripts/m4-ai.ps1 reconcile --month 2026-10` (2026-10-19). Google faturası ay kapanınca kullanıcı tarafından girilir: `reconcile --month 2026-10 --invoice-try <tutar> --usd-try <kur>`. Fark %10'u aşarsa fiyat tablosu gözden geçirilir ve olay audit'e yazılır (ADR-0010).

# AI harcama mutabakatı — 2026-10

- Fiyat tablosu: `gemini-list-prices 2026-10-19 (sources.md S-G2, S-G9)` (liste fiyatı, USD)
- Ledger toplamı: **0.146228 USD** · çağrı 102 · girdi token 185285 · çıktı token 36257
- Fatura: girilmedi (mutabakat bekliyor)

| Görev sınıfı | Tutar |
|---|---|
| (none) | 0.000856 USD |
| HypothesisGeneration | 0.145372 USD |

| İş | Tutar |
|---|---|
| hg-pilot-270fe2bf4ac2 | 0.032664 USD |
| qualification-89075de83cd6 | 0.112708 USD |
| spike-20261018T215804Z | 0.000000 USD |
| spike-20261018T221822Z | 0.000856 USD |

| Gün | Tutar |
|---|---|
| 2026-10-18 | 0.095625 USD |
| 2026-10-19 | 0.050603 USD |
