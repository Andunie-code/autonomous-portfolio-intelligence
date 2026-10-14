# M2 Baseline Sonuçları (m2-protocol v1)

- Durum: **Kullanıcı tarafından kabul edildi (2026-10-14).** Ön kayıt `296757f`; sonuçlar engine `452fabd` (rapor) ve `73a6b60` ile üretildi; dört strateji koşusunun sonuç hash'leri iki engine commit'inde aynıdır (determinizm).
- Veri: `core-4y-S` (20 çekirdek parite, survivor-conditioned), 4h, R = [2022-01-01, 2026-01-01). Maliyetler (cost-model v1) düşülmüştür.
- Bu tablo yalnızca betimleyicidir: kapı eşikleri ve istatistiksel değerlendirme M3'e aittir. Evren hayatta kalma koşuluyla seçildiği için mutlak getiriler iyimser yönde sapmış olabilir.
- Benchmark'larda "İşlem" ve "Kazanma" sütunları anlamlı değildir (alım-satım bacakları kapalı işlem olarak sayılmaz).

| Koşu | Toplam getiri | CAGR | Maks. DD | Sharpe | İşlem | Kazanma | Ort. maruziyet | Devir | Ücret | Slippage | Halt |
|---|---|---|---|---|---|---|---|---|---|---|---|
| ema-trend (policy) | -13.22 % | -3.48 % | 17.98 % | -0.59 | 10 | 0.0 % | 0.9 % | 3.8 | 33 | 38 | 2022-02-12 |
| ema-trend (no-halt) | -55.44 % | -18.30 % | 57.94 % | -0.39 | 295 | 20.7 % | 44.5 % | 96.7 | 614 | 572 | — |
| supertrend (policy) | -18.12 % | -4.87 % | 20.74 % | -0.70 | 20 | 20.0 % | 0.7 % | 7.5 | 62 | 76 | 2022-01-26 |
| supertrend (no-halt) | -69.46 % | -25.66 % | 70.44 % | -0.58 | 793 | 30.6 % | 42.4 % | 259.3 | 1,230 | 1,536 | — |
| buy-and-hold BTCUSDT (benchmark) | 89.08 % | 17.26 % | 67.21 % | 0.57 | 0 | 0.0 % | 100.0 % | 2.4 | 29 | 14 | — |
| equal-weight-20 (benchmark) | -65.41 % | -23.31 % | 79.73 % | 0.01 | 0 | 0.0 % | 100.0 % | 8.1 | 40 | 20 | — |
| cash (benchmark) | 0.00 % | 0.00 % | 0.00 % | 0.00 | 0 | 0.0 % | 0.0 % | 0.0 | 0 | 0 | — |


Sonuç hash'leri:
- ema-trend (policy): `756c8fe803a9b0663be248408bb801e6c74f01e003ac141d942f2dc9e4c3e82a`
- ema-trend (no-halt): `12a38d32eee2047af51853f96c6d186ebc66281f96cf0c7bf0e52374788683b2`
- supertrend (policy): `ff8d0a951f32a7afe806061ffb7725bcf55c2216519304abaeebbd5b0668ad18`
- supertrend (no-halt): `48c935fc1c9a4e150d0c7d3eaca974fe91105cf75918381e77d53d8c4482adb7`
- buy-and-hold BTCUSDT (benchmark): `820587eaba602f6ac849dbbd988601b8a16d88e4885c56a1bfa0c2f50d89c326`
- equal-weight-20 (benchmark): `23c04ca15446d4c838a5c90e02f50acef2ff718fa6855a6608c2be608e114ab1`
- cash (benchmark): `3f3227dbaaa1c70a9b2968cc093d053eb27bc3ceb33ee3c557ff6359a86d25bd`

## Gözlemler (yorum değil, okuma notu)
- Her iki baseline'ın birincil (`policy`) koşusu, Ocak–Şubat 2022'de %15 drawdown halt'ına girdi ve sonrasında yeni giriş açmadı.
- Tanısal (`no-halt`) koşularda her iki baseline da nakit ve BTC buy-and-hold'un altında; eşit ağırlıklı sepetle benzer düzeyde kaldı.
- `research.experiments` tablosunda bu koşular iki kez kayıtlı (ilk koşu ve benchmark devir düzeltmesinden sonraki yeniden koşu; aynı sonuç hash'leri).
