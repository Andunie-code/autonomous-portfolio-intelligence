# Kohort 1 — aktivasyon raporu (M5b Kapı C)

- Durum: **Aktive edildi ve doğrulandı (kullanıcı koşuları, 2026-10-21).** Altı kabul kriteri karşılandı. Tek açık nokta: migration 0008'den sonraki ilk döngünün `ok` olduğunun teyidi (§7).
- Ön kayıt: [`cohort-1-preregistration.md`](cohort-1-preregistration.md) (D31, ADR-0019). Manifesto: `docs/cohorts/cohort-1.json`, commit `6b6f912`.
- Bu rapor yalnızca kayıt ve operasyonel alanları içerir. Fiyat, PnL ve pozisyon yoktur. Açığa çıkmamış Kohort 1'in paper sonuçları araştırma ya da hipotez girdisi değildir (CLAUDE.md).

## 1. Koşular
| # | Koşu | Sonuç |
|---|---|---|
| 1 | `m5-cohort1-release.ps1` | digest `sha256:0a16540e2ba2ee036f324da1a4b05734249b2bd257282841c9c29bf80ed9f739`, engine `91851b634f7d451665a18a34cdb211b5c1c577a8`, `HoldoutEnvironmentVerified = t` |
| 2 | `m5-research.ps1 cohort-manifest` (Claude Code, `research_app`) | Manifesto üretildi; üç üyenin no-halt hash'i M4 kayıtlarıyla aynı; deneme sayacı 98'de kaldı. Kullanıcı onayıyla commit'lendi (`6b6f912`) |
| 3 | `parity-m5` kuru koşu (Claude Code, `R`) | L1 `8dccb8f86868` ve L2 `3bcc41534577` birebir; reconciliation temiz |
| 4 | `m5-cohort1-activate.ps1`, 1. koşu | 1–3. adımlar: kayıt **başarılı**. Script, yanıtı ayrıştırırken 3/7'de durdu (§3) |
| 5 | `m5-cohort1-activate.ps1 -CohortId 2`, 2. koşu | Sunucu kaydı doğrulandı (üyeler, release, `kind = normal`); 4–7. adımlar tamamlandı |
| 6 | `m5-update.ps1` | Migration 0008 uygulandı (`Applied 1, already applied 7`); Trading.Host yeniden derlendi; `-ChecksOnly` ve paper raporu (§7) |

## 2. Kayıt (sunucunun atadığı alanlar)
| Alan | Değer |
|---|---|
| `cohortId` | 2 (holdout kaydında; Kohort 0 = 1) |
| `manifestHash` | `3d5f7c9523196da1c5d2c5877783406355d69ffe0058416b3e3a21dee3cf4736` |
| `registered_at` | 2026-10-21 17:41:45.70675 UTC |
| `A_1` (pencere başı) | 2026-10-23 00:00:00 UTC (`registered_at` + 24 saatten sonraki ilk UTC gece yarısı) |
| Pencere sonu | 2027-10-23 00:00:00 UTC (12 ay) |
| `protocol_complete` / `underpowered` | `t` / `t` |
| `eligibleForValidation` (POST yanıtı) | `True` |

`eligibleForValidation = True` yalnızca protokol alanlarının eksiksiz olduğunu belirtir. Evaluator `underpowered = t` olan bir kohorta `Validated` vermez; beklenen en iyi sonuç `Inconclusive`'dir (D31).

## 3. Parity replay (`R`, aktivasyon koşusu)
- L1 sinyal hash'i: kernel `8dccb8f86868` = paper `8dccb8f86868` (**EQUAL**).
- L2 karar hash'i: kernel `3bcc41534577` = paper `3bcc41534577` (**EQUAL**).
- Reconciliation temiz. Kuru koşuda 9.844 adım, 16 restart ve 472 dolum vardı; D29 tavanı hiç bağlamadı.
- Rapor `m5-parity-cohort2-20261021T184514Z.json`, SHA-256 `2f234be0991108b677f08c294e124370e7a22d2c9871ff9ea1b14d0514d6717e`. Bu özet, Claude Code'un kuru koşusunun rapor özetiyle aynıdır (rapor deterministik).
- Bundan sonra `--record` ile üyeler `Frozen → PaperTrading` geçti.

## 4. Kabul kriterleri (`cohort-1-preregistration.md` §5)
| # | Kriter | Kanıt | Sonuç |
|---|---|---|---|
| 1 | Manifesto `ManifestParser` ve `CohortManifestReader` tarafından kabul; tanım hash'leri eşleşiyor | Evaluator kaydı kabul etti ve aynı `manifestHash`'i döndürdü; `freeze-import` manifestoyu okudu; 2. koşuda sunucu kaydının tanım hash'leri manifestoyla karşılaştırıldı | ✅ |
| 2 | `POST /cohorts` başarılı; `registered_at`, `A_1` ve pencere sonu sunucuda atandı | §2 | ✅ (üyelerin `ProspectiveSealed` durumu bu raporda ayrıca gösterilmedi) |
| 3 | `freeze-import` ve `parity-m5` (`R`): L1/L2 birebir, reconciliation temiz; sonra `--record` | §3 | ✅ |
| 4 | Kohort 1 için ayrı paper run; Kohort 0'ın run'ı değişmiyor | `-ChecksOnly`: `lifecycle cohort=2 members=3 paperTrading=3/3`. Dashboard'da iki ayrı run. Audit'te `paper_run_created:2` (Kohort 0 ve Kohort 1). İlk aktivasyon raporundaki `5/3` satırı bir sayım hatasıydı (§5) | ✅ |
| 5 | D29 `RiskConfig` sürümü eklendi | Çalışan sürüm 0.70 / 0.60 / 0.54; audit'te `risk_config_version_added:1` | ✅ |
| 6 | Operasyonel raporda gecikmeli döngü 0, reconciliation temiz, Trading.Host < 384 MiB | Raporda `late_cycles(>60s)=0`, reconciliation 33/33 temiz. Trading.Host 48,86 MiB / 384 MiB. Döngülerden 2'si `error` (migration öncesi; §7) | ✅ (ilk döngü teyidiyle) |

Raporlar:
- aktivasyon: `m5-cohort1-activation-20261021T184605Z.txt`, SHA-256 `1d213cb9f45d2383a260e5a6c30f654645011c04810db7a0fdefb0e37bd1081d`;
- kontrol (`-ChecksOnly`): `m5-cohort1-activation-20261021T191406Z.txt`, SHA-256 `5a7c85d4e8dce16631035cce3f5fbdc1dca151d485490a089923d7cc00d1e306`;
- paper raporu: `m5-paper-report-20261021T191408Z.txt`, SHA-256 `579744cc60c70f5567b9d193926161467737898498b722bb0c05e7aea77471e8`.

## 5. Script düzeltmeleri
- **Yanıt ayrıştırma (`decc1b2`):** PowerShell `-match` büyük/küçük harf duyarsızdır. tr-TR kültüründe `cohortId`'deki `I`, `[A-Za-z]` dışındaki `ı`'ya katlanır; `cohortId` satırı bu yüzden okunamadı. Ayrıştırma `-cmatch` ile duyarlı hale getirildi. `-CohortId <id>` seçeneği eklendi: kayıt yeniden gönderilmez, sunucu kaydı doğrulanır. `freeze-import` tekrarlanabilir hale getirildi. Aynı `-cnotmatch` düzeltmesi `m5-cohort1-release.ps1`'e de uygulandı.
- **Yaşam döngüsü sayımı (bu commit):** Windows PowerShell 5.1, JSON dizisi döndüren `Invoke-RestMethod` sonucunu tek nesne verir. `@(...)` bu nesneyi tek öğe saydığı için kohort filtresi bütün satırları geçirdi; sayım tüm kohortların `PaperTrading` geçişlerini topladı (2 + 3 = 5). Sonuç önce bir değişkene atanıyor; böylece pipeline satırları tek tek dolaşıyor. Eski ve yeni kod sentetik bir `/lifecycle` yanıtıyla karşılaştırıldı: eski 5, yeni `members=3 paperTrading=3`. Script'in sonundaki uyarı ve çıkış kodu 1 bu yanlış sayımdan geliyordu.
- **`-ChecksOnly -CohortId <id>`:** Yalnızca önkontrolü ve 7. adımı (salt-okunur Ops API çağrıları) çalıştırır. Hiçbir şeyi yeniden başlatmaz ya da değiştirmez.

## 6. İlk tarayıcı kontrolü (kullanıcı, 2026-10-21)
- İki ayrı run görünüyor; beş strateji `PaperTrading` durumunda. Bu gözlem kriter 4'ün ayrı run kısmını karşılıyor; 3/3 sayımı `-ChecksOnly` satırıyla kesinleşecek.
- Bulunan hatalar:
  - `/system` HTTP 500 verdi. Migration 0008 canlı veritabanına uygulanmamıştı; aktivasyon script'i `m5-migrate.ps1`'i çağırmıyordu.
  - Henüz adım atmamış Run 2'nin nakdi toplamda 0 sayıldı.
  - Radar metinleri anlaşılmıyordu.
- Düzeltmeler CHANGELOG'da. Tümü `scripts/m5-update.ps1` ile uygulanır.
- 0008 olmadan paper trader'ın döngüleri de hata veriyor olabilir: `ops_actions.run_id` kolonu yok. Run 2'nin "Son mum: —" görünmesi bununla tutarlı. Güncellemeden sonraki paper raporunda `cycle_error` sayısı ve ilk döngünün sonucu kontrol edilir.

## 7. Güncelleme sonrası doğrulama (`m5-update.ps1`, 2026-10-21 19:14 UTC)
- **Migration:** 0008 (`m5b_runs_dashboard`) uygulandı; önceki 7 migration zaten uygulanmıştı. Bu, §6'daki `/system` 500 hatasının kök nedeninin eksik migration olduğunu doğruluyor.
- **Kontroller:**
  - `lifecycle cohort=2 members=3 paperTrading=3/3`;
  - `killSwitch=False`, `reconciliationOk=True`;
  - `clusterCap=0.70/0.60`, uyarı `0.54`.
- **Operasyonel rapor:**
  - 34 döngü; süre medyanı 159 ms, p95 8.417 ms, en çok 10.405 ms; gecikmeli döngü 0.
  - Sonuçlar `ok:29`, `degraded:3`, `error:2`.
  - Reconciliation 33 koşu, 0 hata; duplicate dolum 0; negatif nakit 0; canlı mum anomalisi 0.
  - Audit: `cycle_error:2`, `paper_run_created:2`, `risk_config_version_added:1`, kill switch aç/kapa 1/1.
  - Bellek: Trading.Host 48,86 MiB / 384 MiB; PostgreSQL 96,34 MiB / 1 GiB.
- **`lastCycleOutcome=error` hakkında:**
  - Raporun gösterdiği: iki `cycle_error`, migration 0008'den önceki döngülerdir. Paper trader her döngüde `ops_actions.run_id`'yi okur ve bu kolon 0008 ile geldi.
  - Trading.Host açılışta beklemeden bir döngü çalıştırır. Kontroller `/health/live` yanıt verir vermez çalıştı. `/status` bu yüzden büyük olasılıkla eski container'ın son döngüsünü gösterdi; açılış döngüsü henüz bitmemişti.
  - `m5-update.ps1` artık kontrollerden önce, yeniden başlatmadan sonra biten ilk döngüyü bekliyor. O döngü `error` ise kontrolleri çalıştırmadan duruyor.
  - **Teyit gerekiyor:** migration sonrası ilk döngünün `ok` olduğu. Kontrol yöntemi: `-ChecksOnly`'yi yeniden çalıştırmak ya da 20:00:15 UTC döngüsünden sonra paper raporunu almak. Beklenen sonuç: `lastCycleOutcome=ok` ve `cycle_error` sayısının 2'de kalması.

## 8. Kullanıcıya kalan kontroller
1. `powershell -ExecutionPolicy Bypass -File scripts/m5-cohort1-activate.ps1 -CohortId 2 -ChecksOnly`. Beklenen: `lastCycleOutcome=ok`. İstersen 20:00:15 UTC'den sonra `m5-paper-report.ps1` de çalıştırılabilir; beklenen `cycle_error:2`.
2. Tarayıcıda yeni dashboard. Dikkat edilecekler:
   - Run 2'nin "İlk 4h mum kapanışı bekleniyor" durumundan çıkması;
   - `/system`'in hatasız açılması;
   - radar metinleri.
