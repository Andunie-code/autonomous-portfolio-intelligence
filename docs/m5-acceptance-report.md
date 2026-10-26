# M5 kabul raporu — Yerel holdout ortamı ve deterministik paper trading

- Durum: **Tamamlandı (kullanıcı kabulü, 2026-10-20).** Ön kayıt: `m5-protocol.md` v1 (`9dda841`). Kapı B: 488/488 test, kullanıcı kabulü. Kapı C: bu rapor.
- Kapanışta `verify.ps1` yeşil: **492 test** (Architecture 87, MarketData 97, HoldoutEvaluator 46, Trading 19, Infrastructure 19, Backtest 113, Ai 111); build 0 uyarı / 0 hata; secret scan geçti.

## 1. Kabul kriterleri (roadmap M5, `m5-protocol.md` §13)
| Kriter | Kanıt | Sonuç |
|---|---|---|
| H-ENV (H5, H6, H6b, H14, H19–H28) gerçek yerel ortamda; `HoldoutEnvironmentVerified` kullanıcı oturumuyla set edildi ve audit'te | Kullanıcının `m5-henv.ps1` koşusu: 79 satır, 79 PASS, gerçek DB'de H22 dahil. Rapor `m5-henv-20261020T145336Z.txt`, SHA-256 `df3b9fb0…227ea`. Bayrak, rapor hash'i ve evaluator digest'iyle set edildi ve audit'e yazıldı | ✅ |
| Research canlı mumları ve açığa çıkmamış paper sonuçlarını okuyamıyor; `research.cohort_status`'a yazamıyor; `eval_*` rolleri `ops`'a bağlanamıyor; erken import reddediliyor | H6b, H25, H21 ve H22 PASS (gerçek ortam). Kapı B'de DB analogları da geçti | ✅ |
| `status_relay` kimliği yalnızca evaluator'da; Research.Host'ta yok; rol yalnızca `cohort_status`'a ekleme yapabiliyor | H25 (`henv-credentials`) ve H21 PASS | ✅ |
| Servisler bellek sınırları içinde; ağır iş sırasında Trading.Host döngüsü kaçırılmıyor | Ölçüm: Trading.Host ≈ 40 MiB / 384 MiB, PostgreSQL ≈ 293 MiB / 1 GiB. Paper çalışmasında `calibrate-quick` koşuldu; raporda `late_cycles(>60s)=0`, `bars_processed_late=0`, `downtime=none` (4 döngü). Sınırlama: ağır işin bir döngü zamanıyla örtüştüğü ayrıca gösterilmedi; Research.Host ve evaluator'ın tepe belleği ölçülmedi | ✅ (sınırlamayla) |
| Deneysel parite tavanı için risk tablosu testleri | Kapı B: tam sınır, kırpma, ret, zorunlu satış yok, çıkış serbest, diğer limitler değişmez, yetkisiz değişikliğin reddi (DB rolü ve API); H23 gerçek ortamda | ✅ |
| Operasyonel güvenlik eylemleri çalışıyor, audit'te ve kohort kayıtlarını değiştirmiyor | Kapı B testleri (kill switch, devre dışı bırakma, tümünü kapatma; V9). Gerçek ortamda kill switch aç/kapa denendi (`audit=…trading_kill_switch_engaged:1,trading_kill_switch_released:1`) | ✅ |
| Parity replay L1/L2 geçiyor; L3 normalize; stop kuralı paper = backtest; bayat veride yalnızca çıkış; restart tutarlı | `m5-parity-results.md`: 9.844 adım ve 16 restart sonunda L1 `f870c0a6…` ve L2 `b078ebaa…` birebir; P1 tavanıyla tekrar koşuda da birebir. Gerçek ortamda planlı restart sonrası reconciliation 2/2 temiz | ✅ |
| 7 gün paper; kesintiler raporlanıyor; reconciliation ile tutarlı devam; yedekten geri yükleme | **Sapma (U):** paper kapanış anında yeni başlamıştı. 7 günlük gözlem, kullanıcı kararıyla kapanıştan sonra arka planda süren operasyonel izleme olarak devam ediyor. Geri yükleme: 33/33 tablo `MATCH` (yedek `ops-20261020T151039Z.dump.enc` ve kayıt dışa aktarımı) | Kısmen; kabul edilmiş sapma |
| P1 analiz raporu sunuldu | `m5-p1-analysis.md`; kullanıcı kararı D29 | ✅ |

## 2. İlk operasyonel paper raporu (Claude Code'a görünen alanlar)
Rapor `m5-paper-report-20261020T151421Z.txt`, SHA-256 `8955efe9…6e11a2`:
- **Döngüler:** 4; süre medyanı 1.103 ms, p95 9.100 ms, maksimum 10.405 ms; gecikmeli döngü 0.
- **Mumlar:** 269 işlendi; catch-up döngüsü 2, bayat döngü 1. Bunlar ilk açılıştaki 45 günlük ısınma backfill'idir: işlem başlangıcından önceki mumlar bayat sayıldı, girişi etkilemedi. 5 dakikadan geç işlenen mum 0.
- **Sonuçlar:** `degraded:2, ok:2`. `degraded`, ısınmadaki bayat veriden ve kill switch denemesinden geliyor; beklenen durum.
- **Bütünlük:** reconciliation 2/2 temiz; duplicate dolum 0; negatif nakit 0; canlı mum anomalisi 0; kesinti yok.

## 3. Kararlar ve belge güncellemeleri
- **D29 (P1, U):** operasyonda bağlayıcı küme tavanı %60 (ρ* = 0,70), uyarı %54.
  - Yeni paper koşuları tavanla başlar.
  - Çalışan koşuya tavan, kullanıcının Ops API üzerinden ekleyeceği yeni bir `RiskConfig` sürümüyle girer (§4).
  - Kohort 0'ın tarihsel parity replay'inde tavan hiç bağlamadı; L1/L2 hash'leri değişmedi.
- **D30 (U):** `PaperTrading → TestnetActive` için asgari paper süresi: ≥ 14 gün, ≥ 60 işlenmiş 4h mum, son 7 günde sıfır kritik hata. Geçiş yine kullanıcı onayı gerektirir.
- **`mem_limit` ve `cpus` (T):** değerler korundu. Research.Host ve evaluator'ın tepe kullanımı M10'daki 30 günlük provada ölçülür.
- **Evaluator yayın tamponu:** 3 gün korundu. M5'te pencere import'u olmadığı için ölçülemedi.
- **Güncellenen belgeler:** `requirements.md`, `risk-model.md`, `roadmap.md`, ADR-0012/0013/0014/0015'in uygulama durumları, CHANGELOG.

## 4. Kullanıcıya kalan işlem: P1 tavanının çalışan paper koşusuna eklenmesi
Kapanış commit'indeki kod, `RiskConfig` belgesine küme tavanı alanlarını ekler. Çalışan Trading.Host'un bu kodla yeniden derlenmesi ve yeni sürümün eklenmesi gerekir. Tavan eklemek bir sıkılaştırmadır; `confirm` gerektirmez.

```powershell
powershell -ExecutionPolicy Bypass -File scripts/m5-paper-start.ps1   # yeniden derler; canlı veri açık kalır
$t = Get-Content secrets\ops_api_token
$body = '{"config":{"maxOpenPositions":5,"maxAssetWeight":0.20,"maxTotalExposure":0.80,"dailyLossLimit":0.03,"maxDrawdown":0.15,"drawdownHaltEnabled":true,"maxPositionLoss":0.10,"experimentalPairsMaxExposure":0.10,"maxDataLagBars":2,"clusterCapThreshold":0.70,"clusterCapMaxExposure":0.60,"clusterWarningShare":0.54},"reason":"P1 / D29 (2026-10-20)","confirm":false}'
Invoke-RestMethod -Method Post http://127.0.0.1:55490/risk-config -Headers @{Authorization="Bearer $t"} -ContentType 'application/json' -Body $body
```

Sonraki operasyonel raporda `audit=` satırında `risk_config_version_added:1` görülmelidir.

## 5. Açık konular ve sonraki adımlar
- **Paper gözlemi sürüyor:** günlük `m5-paper-report.ps1`. D30 için gereken 14 gün ve 60 mumluk süre bu gözlemle dolar.
- **Kohort 0:** ileriye dönük penceresi 2027-10-17'de kapanır. Evaluator ilk gerçek arşiv import'unu `window end + 3 gün` sonra yapar; ilk `Validated / Failed / Inconclusive` sonucu o zaman gelir. Kohort 0 gücü yetersiz olarak işaretli; en iyi ihtimalle `Inconclusive` olur.
- **M6 (Binance Testnet):** ön koşulları B6 (anahtarın IP kısıtı) ve `sources.md`'de stop emri tipi ile Testnet desteğinin doğrulanması. Ayrı kullanıcı onayıyla başlar. **Güncelleme (2026-10-26):** M6 başladı; B6 → D33.

## 6. Arka plan paper gözlemi (2026-10-26)
Kullanıcı 5 günlük aradan sonra sistemi açtı ve `m5-paper-report.ps1` çalıştırdı (2026-10-26T18:48:00Z). Rapor SHA-256: `9869e507b900326a9256b655254e9043b96c1e354394449320b19c621fa7f9d6`. Burada yalnızca operasyonel alanlar yer alır (m5-protocol §7.3).

- **Döngüler:** 40 döngü; gecikmeli (> 60 sn) döngü 0; sonuçlar `ok:33, degraded:5, error:2`. İki `error` döngüsü migration 0008'den önceye aittir (CHANGELOG 2026-10-21).
- **Mumlar:** 617 mum işlendi; catch-up döngüsü 5, bayat döngü 4.
- **Kesintiler:**
  - 2026-10-20T23:00Z – 2026-10-21T09:48Z;
  - 2026-10-21T23:00Z – 2026-10-26T18:46Z.
  - Kaçırılan mumlar açılışta sırayla işlendi.
- **Bütünlük:**
  - reconciliation: 43 koşu, 0 tutarsızlık;
  - duplicate dolum 0; negatif nakit 0; canlı mum anomalisi 0.
- **Bellek:** Trading.Host 59,84 MiB / 384 MiB; PostgreSQL 47,13 MiB / 1 GiB.
- **Dashboard (kullanıcı beyanı):**
  - Kohort 1'de ilk canlı giriş sinyali ve L2 `Approved` kararı oluştu.
  - Kesinti sırasında geçmiş mumlarda oluşan sinyaller `stale-data` kuralıyla doğru şekilde reddedildi.
  - Sembol, fiyat ve PnL kaydedilmez. Açığa çıkmamış kohortun bu bilgisi araştırma ya da hipotez girdisi olarak kullanılmaz (CLAUDE.md).
- **D30 etkisi** (U-M6-3, takvim günü):
  - en erken uygunluk Kohort 0 için 2026-11-03, Kohort 1 için 2026-11-04;
  - 60 mum şartı ve son 7 günde 0 kritik hata şartı ayrıca aranır;
  - bu rapor döneminde kritik hata yok.

