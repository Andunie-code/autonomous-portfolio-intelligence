# M4 LLM'siz araştırma sonuçları (m4-protocol v1)

> **Sonuç:** 12 hipotez ve 96 grid noktası uçtan uca işlendi. 0 hipotez tüm araştırma kapılarını geçti; hiçbir aday yok. Güvenlik ihlali: 0. AI çağrısı yapılmadı (0 USD).
>
> Bu, roadmap M4'teki boru hattı kriteridir (istatistiksel değildir). Koşu ön kayda göre bir kez yapıldı; sonuç olduğu gibi yazılır (m4-protocol §7).

- Ön kayıt: `m4-protocol v1`, commit `32cf7c5`. Hipotez listesi `m4-hypotheses v1`, liste hash'i `89ab3965a8cbaf068788a95bc354f0340c29ae1b8d3834ef6c840bad1d4c2ec7` (yüklemede yeniden hesaplandı ve doğrulandı).
- Engine: `32cf7c5+src-tree:d47021c55c1bb310c0e7b49ec4dc5b1a67281d90`. Veri: `core-4y-S` (20 parite, snapshot `a6c4e89b9836…`), 4h, R = [2022-01-01, 2026-01-01). Yerel dev DB; ağ erişimi yok.
- Global deneme sayacı: koşudan önce 2, sonra **98** (tavan 100). G6'da N = 98.
- Kapılar `no-halt` koşusunda, v2 eşikleriyle: `N_min = 100`, `τ_wf = 0,75` (G3: 2023–2025 ankore genişleyen pencere), `τ_nb = 0,80`, `τ_dsr = 0,90`. Maliyet modeli v1, D25 hard stop. Bootstrap B = 10.000, seed 20260101.
- Köken: hipotez `dev-assisted` (Claude Opus 5.5, bilgi kesimi 2026-06; tanısal pencereyle çakışma bayrağı), grid noktaları `grid`.
- Süre: 00:01:08.

## Hipotez özeti (seçilen aday, `no-halt`)

| # | Hipotez | Seçilen nokta | M_R | İşlem | %95 alt sınır | G3 (ankore) | G4 komşuluk | DSR olasılığı | G1 | G2 | G3 | G4 | G5 | G6 | Sonuç |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| H01 | Donchian kırılımı | `n100-m2` | -1.8 % | 338 | — | 0.67 | 0.13 | 0.004 | ✗ | ✗ | ✗ | ✗ | ✓ | ✗ | Reddedildi |
| H02 | Bollinger ortalamaya dönüş | `p60-k2p5` | -29.6 % | 596 | — | 0.00 | 0.00 | 0.000 | ✗ | ✗ | ✗ | ✗ | ✗ | ✗ | Reddedildi |
| H03 | Trend içinde RSI ortalamaya dönüş | `r10-t25` | -3.4 % | 158 | — | 0.67 | 0.00 | 0.001 | ✗ | ✗ | ✗ | ✗ | ✓ | ✗ | Reddedildi |
| H04 | Sıfır çizgisi üstünde MACD kesişimi | `f12-s26-g12` | 8.3 % | 1167 | -17.1 % | 0.67 | 1.00 | 0.025 | ✓ | ✗ | ✗ | ✓ | ✓ | ✗ | Reddedildi |
| H05 | ADX filtreli trend | `t30-e200` | -3.2 % | 718 | — | 0.67 | 0.10 | 0.003 | ✗ | ✗ | ✗ | ✗ | ✓ | ✗ | Reddedildi |
| H06 | Zaman serisi momentumu (ROC) | `n42-m3` | -11.5 % | 2131 | — | 0.67 | 0.00 | 0.001 | ✗ | ✗ | ✗ | ✗ | ✗ | ✗ | Reddedildi |
| H07 | Hacim teyitli kırılım | `n50-v2p5` | 3.9 % | 554 | -20.3 % | 0.33 | 0.92 | 0.012 | ✓ | ✗ | ✗ | ✓ | ✓ | ✗ | Reddedildi |
| H08 | BTC rejim filtreli SMA trendi | `n150-b300` | 22.8 % | 727 | -9.4 % | 0.67 | 1.00 | 0.110 | ✓ | ✗ | ✗ | ✓ | ✓ | ✗ | Reddedildi |
| H09 | Bollinger sıkışma kırılımı | `w0p08-k2` | -11.7 % | 1194 | — | 0.33 | 0.00 | 0.000 | ✗ | ✗ | ✗ | ✗ | ✓ | ✗ | Reddedildi |
| H10 | EMA trendinde geri çekilme sonrası giriş | `f30-s120` | 0.7 % | 795 | -28.0 % | 0.33 | 0.25 | 0.007 | ✓ | ✗ | ✗ | ✗ | ✓ | ✗ | Reddedildi |
| H11 | RSI momentumu | `r14-t65` | 0.3 % | 761 | -32.6 % | 0.67 | 0.40 | 0.006 | ✓ | ✗ | ✗ | ✗ | ✓ | ✗ | Reddedildi |
| H12 | BTC rejim filtreli MACD histogramı | `f12-s26-b100` | 4.0 % | 1430 | -23.2 % | 0.67 | 1.00 | 0.012 | ✓ | ✗ | ✗ | ✓ | ✓ | ✗ | Reddedildi |

## Betimleyici: seçilen adayın `policy` koşusu ve benchmark'lar (kapı değil)

| # | M_R (policy) | İşlem | Maks. DD (policy) | Halt | Maks. DD (no-halt) |
|---|---|---|---|---|---|
| H01 | -4.0 % | 11 | 15.7 % | 2022-02-21 | 57.2 % |
| H02 | -5.3 % | 20 | 19.0 % | 2022-01-23 | 72.8 % |
| H03 | -0.4 % | 103 | 15.2 % | 2024-08-04 | 28.9 % |
| H04 | 0.2 % | 98 | 15.6 % | 2022-06-08 | 37.5 % |
| H05 | -3.7 % | 21 | 17.8 % | 2022-03-03 | 46.8 % |
| H06 | -4.5 % | 30 | 17.8 % | 2022-01-21 | 53.1 % |
| H07 | -1.2 % | 43 | 18.1 % | 2022-06-01 | 37.7 % |
| H08 | -4.2 % | 34 | 16.5 % | 2022-03-04 | 38.6 % |
| H09 | -0.6 % | 76 | 15.7 % | 2022-05-23 | 61.2 % |
| H10 | -4.1 % | 22 | 16.1 % | 2022-02-14 | 45.3 % |
| H11 | -3.6 % | 15 | 16.7 % | 2022-01-31 | 56.6 % |
| H12 | 2.4 % | 89 | 15.5 % | 2022-06-07 | 44.6 % |

- Benchmark `buy-and-hold BTCUSDT`: M = 15.9 %, toplam getiri 89.1 %, maks. DD 67.2 %.
- Benchmark `equal-weight-20`: M = -26.5 %, toplam getiri -65.4 %, maks. DD 79.7 %.
- Nakit (birincil benchmark): M = 0.

## Grid noktaları (`no-halt`, M_R)

**H01 — Donchian kırılımı.** Ankore katmanlar: 2023: seçim `n100-m2` → test 33.3 %; 2024: seçim `n100-m3` → test 32.7 %; 2025: seçim `n100-m3` → test -28.8 %.

| Nokta | M_R | İşlem | Sonuç hash'i |
|---|---|---|---|
| `n20-m2` | -19.7 % | 1060 | `56a2c1fbe9b4` |
| `n20-m3` | -11.3 % | 975 | `2f5aacc4555f` |
| `n20-m4` | -9.1 % | 957 | `1ef300a1ca03` |
| `n55-m2` | -11.9 % | 533 | `25935423b84f` |
| `n55-m3` | -12.5 % | 454 | `579b16e71733` |
| `n55-m4` | -6.1 % | 425 | `b11cf472879a` |
| `n100-m2` ★ | -1.8 % | 338 | `be27f6f20376` |
| `n100-m3` | -3.3 % | 273 | `2d30caa675dc` |
| `n100-m4` | -6.6 % | 248 | `a0ea15a3342a` |

**H02 — Bollinger ortalamaya dönüş.** Ankore katmanlar: 2023: seçim `p60-k2p5` → test -5.2 %; 2024: seçim `p40-k2p5` → test -58.7 %; 2025: seçim `p60-k2p5` → test -24.4 %.

| Nokta | M_R | İşlem | Sonuç hash'i |
|---|---|---|---|
| `p20-k1p5` | -47.2 % | 2221 | `5b5329c127f5` |
| `p20-k2` | -50.5 % | 1640 | `46df6769f775` |
| `p20-k2p5` | -37.9 % | 1017 | `30409820de02` |
| `p40-k1p5` | -40.1 % | 1511 | `dae009ced407` |
| `p40-k2` | -36.2 % | 1054 | `8890ed5294ad` |
| `p40-k2p5` | -44.5 % | 699 | `9c0d6676720d` |
| `p60-k1p5` | -45.2 % | 1200 | `346942eccf64` |
| `p60-k2` | -41.4 % | 886 | `34b1d3436c12` |
| `p60-k2p5` ★ | -29.6 % | 596 | `b49c6c682907` |

**H03 — Trend içinde RSI ortalamaya dönüş.** Ankore katmanlar: 2023: seçim `r10-t30` → test 7.0 %; 2024: seçim `r10-t25` → test -30.6 %; 2025: seçim `r10-t25` → test 3.8 %.

| Nokta | M_R | İşlem | Sonuç hash'i |
|---|---|---|---|
| `r5-t25` | -30.4 % | 938 | `b4de9d756262` |
| `r5-t30` | -35.5 % | 1336 | `02900884d82c` |
| `r5-t35` | -38.1 % | 1840 | `f3afa3e054c4` |
| `r10-t25` ★ | -3.4 % | 158 | `ebc30cc6d2fe` |
| `r10-t30` | -9.6 % | 358 | `a56ae870c880` |
| `r10-t35` | -28.0 % | 628 | `f23ce494686e` |
| `r14-t25` | -4.0 % | 33 | `5fdc3bca23ac` |
| `r14-t30` | -6.1 % | 121 | `d10531c3aee7` |
| `r14-t35` | -6.2 % | 321 | `6682db3d344a` |

**H04 — Sıfır çizgisi üstünde MACD kesişimi.** Ankore katmanlar: 2023: seçim `f12-s26-g12` → test 12.7 %; 2024: seçim `f12-s26-g12` → test 53.1 %; 2025: seçim `f12-s26-g12` → test -35.8 %.

| Nokta | M_R | İşlem | Sonuç hash'i |
|---|---|---|---|
| `f8-s21-g6` | -24.7 % | 2046 | `c12fda0b6e28` |
| `f8-s21-g9` | -11.7 % | 1746 | `76fdc11822e8` |
| `f8-s21-g12` | 2.5 % | 1542 | `ab78dd0caeb5` |
| `f12-s26-g6` | -4.5 % | 1641 | `6a41478faa0d` |
| `f12-s26-g9` | -2.3 % | 1359 | `8ee6bbb7b4ea` |
| `f12-s26-g12` ★ | 8.3 % | 1167 | `eb7bda877eab` |
| `f19-s39-g6` | 1.1 % | 1217 | `cc7334085180` |
| `f19-s39-g9` | -1.4 % | 971 | `2b541ecd1105` |
| `f19-s39-g12` | 4.0 % | 827 | `78e0272c25dd` |

**H05 — ADX filtreli trend.** Ankore katmanlar: 2023: seçim `t25-e200` → test 1.7 %; 2024: seçim `t30-e200` → test 46.6 %; 2025: seçim `t30-e200` → test -22.0 %.

| Nokta | M_R | İşlem | Sonuç hash'i |
|---|---|---|---|
| `t20-e50` | -39.9 % | 1612 | `5a3f9e7a097b` |
| `t20-e100` | -35.0 % | 1409 | `1b96ccc9c026` |
| `t20-e200` | -25.2 % | 1255 | `0ea936e3f4f9` |
| `t25-e50` | -30.0 % | 1241 | `4ac02c3b4b8d` |
| `t25-e100` | -24.4 % | 1079 | `c1038d975b0d` |
| `t25-e200` | -14.4 % | 973 | `8e7158143d3e` |
| `t30-e50` | -11.4 % | 884 | `06e3c882e326` |
| `t30-e100` | -10.2 % | 784 | `b8f16172ea00` |
| `t30-e200` ★ | -3.2 % | 718 | `7db2e1e59525` |

**H06 — Zaman serisi momentumu (ROC).** Ankore katmanlar: 2023: seçim `n42-m4` → test 14.2 %; 2024: seçim `n42-m3` → test 34.4 %; 2025: seçim `n42-m3` → test -55.4 %.

| Nokta | M_R | İşlem | Sonuç hash'i |
|---|---|---|---|
| `n42-m2` | -17.0 % | 2210 | `166f897db5ad` |
| `n42-m3` ★ | -11.5 % | 2131 | `9a0a2fc889da` |
| `n42-m4` | -13.2 % | 2126 | `a66220e9a73b` |
| `n90-m2` | -33.1 % | 1638 | `b5b4db1177ca` |
| `n90-m3` | -31.1 % | 1550 | `60455af7b9ab` |
| `n90-m4` | -30.6 % | 1526 | `2cfe9d561b12` |
| `n180-m2` | -25.5 % | 1315 | `248ffffb0c6b` |
| `n180-m3` | -22.2 % | 1162 | `7addbbcba56e` |
| `n180-m4` | -27.0 % | 1134 | `eb18bb4586f9` |

**H07 — Hacim teyitli kırılım.** Ankore katmanlar: 2023: seçim `n50-v2p5` → test -4.0 %; 2024: seçim `n50-v2p5` → test 58.6 %; 2025: seçim `n50-v2p5` → test -19.5 %.

| Nokta | M_R | İşlem | Sonuç hash'i |
|---|---|---|---|
| `n20-v1p5` | -15.2 % | 1137 | `a94abea19080` |
| `n20-v2` | -9.8 % | 973 | `8751009cfe58` |
| `n20-v2p5` | -11.8 % | 789 | `4172ed51abcd` |
| `n50-v1p5` | -5.4 % | 769 | `06c2dec45b0b` |
| `n50-v2` | 0.7 % | 665 | `c635f4e794bd` |
| `n50-v2p5` ★ | 3.9 % | 554 | `d3ff1713478a` |

**H08 — BTC rejim filtreli SMA trendi.** Ankore katmanlar: 2023: seçim `n50-b200` → test 0.8 %; 2024: seçim `n150-b200` → test 42.5 %; 2025: seçim `n150-b200` → test -30.8 %.

| Nokta | M_R | İşlem | Sonuç hash'i |
|---|---|---|---|
| `n50-b100` | 11.4 % | 1391 | `3f363cfa6735` |
| `n50-b200` | 16.7 % | 1197 | `02ff5cc9ce48` |
| `n50-b300` | 12.8 % | 1153 | `a926db7db534` |
| `n100-b100` | 11.3 % | 1008 | `643cd06b1590` |
| `n100-b200` | 17.0 % | 841 | `7ad197fd3fb6` |
| `n100-b300` | 22.2 % | 779 | `e4c0e644a99a` |
| `n150-b100` | 1.3 % | 816 | `abaf6eaee337` |
| `n150-b200` | 20.6 % | 731 | `547486658d70` |
| `n150-b300` ★ | 22.8 % | 727 | `7712412fe9b4` |

**H09 — Bollinger sıkışma kırılımı.** Ankore katmanlar: 2023: seçim `w0p08-k2` → test -32.9 %; 2024: seçim `w0p08-k1p5` → test 15.6 %; 2025: seçim `w0p12-k1p5` → test -32.1 %.

| Nokta | M_R | İşlem | Sonuç hash'i |
|---|---|---|---|
| `w0p08-k1p5` | -18.0 % | 1635 | `1e87e7fa8af2` |
| `w0p08-k2` ★ | -11.7 % | 1194 | `8582644a9a2d` |
| `w0p12-k1p5` | -11.8 % | 1726 | `8944e04960e5` |
| `w0p12-k2` | -18.3 % | 1332 | `8b2cb466f9ab` |
| `w0p16-k1p5` | -23.8 % | 1771 | `669d5eac4547` |
| `w0p16-k2` | -19.3 % | 1389 | `d6434a13e727` |

**H10 — EMA trendinde geri çekilme sonrası giriş.** Ankore katmanlar: 2023: seçim `f30-s200` → test 57.8 %; 2024: seçim `f30-s200` → test -16.2 %; 2025: seçim `f10-s200` → test -13.7 %.

| Nokta | M_R | İşlem | Sonuç hash'i |
|---|---|---|---|
| `f10-s80` | -18.4 % | 1237 | `c5ecfc77bb1a` |
| `f10-s120` | -4.4 % | 940 | `b2a8afb89710` |
| `f10-s200` | -0.3 % | 669 | `4d7fbf680052` |
| `f20-s80` | -13.8 % | 1096 | `ad8de24e4f8f` |
| `f20-s120` | -2.0 % | 795 | `9ce3a6a6125e` |
| `f20-s200` | 0.1 % | 600 | `2c497f490502` |
| `f30-s80` | -17.6 % | 1128 | `0238a5628dae` |
| `f30-s120` ★ | 0.7 % | 795 | `784e48b30c48` |
| `f30-s200` | -0.3 % | 569 | `4c6815d373b0` |

**H11 — RSI momentumu.** Ankore katmanlar: 2023: seçim `r21-t55` → test 41.7 %; 2024: seçim `r21-t55` → test 23.9 %; 2025: seçim `r14-t55` → test -51.6 %.

| Nokta | M_R | İşlem | Sonuç hash'i |
|---|---|---|---|
| `r14-t55` | -3.7 % | 1259 | `a2864f234a37` |
| `r14-t60` | -13.5 % | 983 | `979906f01959` |
| `r14-t65` ★ | 0.3 % | 761 | `28f59cf1dba9` |
| `r21-t55` | -14.4 % | 779 | `8cefb0e5dba1` |
| `r21-t60` | -15.3 % | 607 | `7ab578739c1b` |
| `r21-t65` | -20.3 % | 455 | `92f0744cf31a` |

**H12 — BTC rejim filtreli MACD histogramı.** Ankore katmanlar: 2023: seçim `f12-s26-b100` → test 11.9 %; 2024: seçim `f12-s26-b100` → test 31.5 %; 2025: seçim `f12-s26-b100` → test -36.6 %.

| Nokta | M_R | İşlem | Sonuç hash'i |
|---|---|---|---|
| `f8-s21-b100` | -3.9 % | 1704 | `c2e3a4a6d9cd` |
| `f8-s21-b200` | -7.6 % | 1617 | `8bbc6b751124` |
| `f12-s26-b100` ★ | 4.0 % | 1430 | `35e696f1e7a8` |
| `f12-s26-b200` | -3.5 % | 1323 | `3abf69a5e31e` |
| `f19-s39-b100` | -8.4 % | 1100 | `da3759d9b3dc` |
| `f19-s39-b200` | 1.8 % | 987 | `8da172c5ac8a` |

## Kapıdan geçemeyenler

- H01: G1 cost-adjusted return, G2 trades and confidence, G3 walk-forward, G4 parameter neighbourhood, G6 deflated Sharpe
- H02: G1 cost-adjusted return, G2 trades and confidence, G3 walk-forward, G4 parameter neighbourhood, G5 multi-asset, G6 deflated Sharpe
- H03: G1 cost-adjusted return, G2 trades and confidence, G3 walk-forward, G4 parameter neighbourhood, G6 deflated Sharpe
- H04: G2 trades and confidence, G3 walk-forward, G6 deflated Sharpe
- H05: G1 cost-adjusted return, G2 trades and confidence, G3 walk-forward, G4 parameter neighbourhood, G6 deflated Sharpe
- H06: G1 cost-adjusted return, G2 trades and confidence, G3 walk-forward, G4 parameter neighbourhood, G5 multi-asset, G6 deflated Sharpe
- H07: G2 trades and confidence, G3 walk-forward, G6 deflated Sharpe
- H08: G2 trades and confidence, G3 walk-forward, G6 deflated Sharpe
- H09: G1 cost-adjusted return, G2 trades and confidence, G3 walk-forward, G4 parameter neighbourhood, G6 deflated Sharpe
- H10: G2 trades and confidence, G3 walk-forward, G4 parameter neighbourhood, G6 deflated Sharpe
- H11: G2 trades and confidence, G3 walk-forward, G4 parameter neighbourhood, G6 deflated Sharpe
- H12: G2 trades and confidence, G3 walk-forward, G6 deflated Sharpe

Tüm deneme ve sonuç kayıtları `research.trials`, `research.experiments` ve `research.hypotheses` tablolarında; ayrıntılı JSON, depo dışındaki rapor dizininde.
