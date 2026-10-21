# Kohort 1 — ön kayıt (operasyonel kohort)

- Durum: **Kapı A onaylandı (U, 2026-10-21).**
- Karar: **D31 (U, 2026-10-21)**; ayrıntılı gerekçe ADR-0019.
- Bu belge manifestodan önce yazılır. Manifesto (`docs/cohorts/cohort-1.json`) Kapı C'de, buradaki değerlerle üretilir ve değiştirilemez.

## 1. Nitelik
- **Yalnızca operasyonel.** Üyeler M4'te araştırma kapılarından G2, G3 ve G6'yı geçmedi (`m4-llmless-results.md`); hiçbiri `Candidate` değildir.
- Manifestoda `power.underpowered: true` ve şu `exemptions` satırı bulunur:
  `operational-override (U, 2026-10-21): research gates G2, G3, G6 not passed (m4-llmless-results); never eligible for Validated`
- Protokol alanları tam ve Kohort 0 ile aynıdır (`m3-protocol v2`, commit `5668d12`; D26). Hiçbir eşik gevşetilmez.
- Beklenen en iyi sonuç `Inconclusive`. `Failed` kuralları geçerlidir.
- Açığa çıkmamış Kohort 1'in paper sonuçları araştırma ya da hipotez girdisi değildir (CLAUDE.md).

## 2. Üyeler
Tanımlar `m4-hypotheses.v1.json`'dan aynen alınır; tanım hash'leri M4'te sayılmış denemelerdir, yeni deneme açılmaz.

| # | Hipotez | Nokta | Ad | definitionHash | structureHash (M4) | Policy M_R | No-halt M_R |
|---|---|---|---|---|---|---|---|
| 1 | H08 BTC rejim filtreli SMA trendi | `n150-b300` | `h08-smabtc-n150-b300` | `e34a7b626331a6e34885109ab2901c8211ec8272b9d4bd0f55cec5d0dfe90a8a` | `069d64c8…b5237c` | −4,2 % | 22,8 % |
| 2 | H04 Sıfır çizgisi üstünde MACD kesişimi | `f12-s26-g12` | `h04-macd-f12-s26-g12` | `44382f29b407f29406ca27dd53e5025fd9b2748269e150d937657b92c4047aad` | `3f28291e…f4ac2d` | 0,2 % | 8,3 % |
| 3 | H07 Hacim teyitli kırılım | `n50-v2p5` | `h07-volbo-n50-v2p5` | `a262a80048f9b32eb9cd143acf738b358b8bb91ed71d8be26fb38ba2bf3cc0e5` | `c748433b…82f3df` | −1,2 % | 3,9 % |

- `researchMetric` = policy koşusunun M_R değeri (Kohort 0 ile aynı kural, `evaluatedRun: policy`); manifest üretici bunu `research.experiments`'tan okur. δ, evaluator tarafından yeniden hesaplanır (D26: alt sınır 0,05).
- **Seçim gerekçesi (U):** üç farklı gösterge ailesi (SMA + BTC rejimi, MACD, Donchian + hacim). H12 yerine H04: BTC rejim filtresi tek üyede kalır ve MACD ailesi tekrarlanmaz.
- Kısa tanımlar (tamamı manifestoda):
  - **H08:** `sma(150)`, BTC `sma(300)`, `atr(14)`. Giriş: `close` `sma150`'yi yukarı keser **ve** BTC kapanışı > BTC `sma300`. Çıkış: `close` `sma150`'yi aşağı keser. Stop: ATR × 3.
  - **H04:** `macd(12, 26, 12)`, `atr(14)`. Giriş: MACD çizgisi sinyali yukarı keser **ve** MACD çizgisi > 0. Çıkış: çizgi sinyali aşağı keser. Stop: ATR × 3.
  - **H07:** `donchian(50)`, `volume_sma(20)`, `ema(20)`, `atr(14)`. Giriş: `close` > Donchian üst **ve** hacim > 2,5 × hacim ortalaması. Çıkış: `close` < `ema20`. Stop: ATR × 2,5.
  - D25 hard stop (%10) tümünde geçerlidir.

## 3. Ortak alanlar
- Pariteler: Kohort 0 ile aynı 20 çekirdek parite (`core-universe.v1`), E1 yok.
- Snapshot `a6c4e89b98364a2e2b3479e81a9e7188ffb27e08a4d444723523e413f7509e82`; aralık 4h; maliyet modeli v1; allocation v1; başlangıç sermayesi 10.000.
- Risk bloğu Kohort 0 ile aynı altı alan. **D29 manifestoya yazılmaz** (D29 metni); paper run'a `RiskConfig` üzerinden girer.
- `power.months` 12; `plannedTrades` ve rejim artefaktları güç aracının çıktısıdır.
- `imageDigest` ve `engineSha`: holdout'ta kayıtlı ve çalışan evaluator release'i (kullanıcının `m5-cohort1-release.ps1` çıktısı).
- `registeredAt` ve `A_1` manifestoda **yoktur**; sunucu atar.

## 4. Bedeller
- Kohort 1, D21'deki ikinci açık kohort hakkını pencere sonu + 3 gün boyunca (~12 ay) tutar. Bu sürede doğrulamaya uygun yeni bir kohort açılamaz.
- H08, H04 ve H07 aileleri ömür boyu 2 ileriye dönük denemeden birini harcar.

## 5. Kabul kriterleri (Kapı C)
1. Manifesto evaluator `ManifestParser`'ı ve Kernel `CohortManifestReader`'ı tarafından kabul edilir; tanım hash'leri yeniden hesaplanıp eşleşir.
2. `POST /cohorts` 201 döner; `registered_at`, `A_1` ve pencere sonu sunucu tarafından atanır ve rapora yazılır; üyeler `ProspectiveSealed`.
3. `freeze-import` ve `parity-m5` (`R`): L1 sinyal ve L2 karar hash'leri iki yolda birebir, reconciliation temiz; L3 raporu üretildi. Ancak bundan sonra `--record` ile `Frozen → PaperTrading`.
4. Trading.Host Kohort 1 için ayrı bir paper run açar; Kohort 0'ın run'ı değişmez.
5. D29 `RiskConfig` sürümü eklendi (audit'te `risk_config_version_added`).
6. Operasyonel raporda gecikmeli döngü 0, reconciliation temiz, Trading.Host belleği < 384 MiB.
