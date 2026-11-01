# M10 Kabul Raporu — Sağlamlaştırma

- Durum: **M10 tamamlandı (kullanıcı kabulü, U, 2026-11-01; kabul edilen sapma, U-M9-5/D35).** 30 günlük Testnet provası başlatıldı ve arka planda süren operasyonel gözlem olarak devam eder; sonuçları bu rapora ek olarak işlenir.
- Ön kayıt: [`m9-m10-protocol.md`](m9-m10-protocol.md) v1 (Kapı A `d202bae`), ADR-0024. Kapı B: `ae630ca` (794 test). Kapanış düzeltmesi: §5.
- Claude Code `/alarms` ve dashboard uç noktalarını gerçek ortamda çağırmadı, `m10-*`, `m5-*` ve `m6-*` script'lerini çalıştırmadı. Aşağıdaki gerçek ortam değerleri kullanıcının paylaştığı operasyonel çıktılardır.

## 1. Kapı C adımları
| # | Adım | Durum |
|---|---|---|
| C0 | `m6-update.ps1` (Kapı B kodu; migration yok) | Tamamlandı (kullanıcı) |
| C1 | Kohort 0 `TestnetActive` (M6 C4) | Tamamlandı: `ema-trend`, `supertrend` (`m6-acceptance-report.md` §7) |
| C2 | İlk Testnet döngüsü ve `-ChecksOnly`; **prova başlangıcı** | Tamamlandı: açık Testnet run 1, bilinmeyen emir 0, korumasız pozisyon 0, kill switch kapalı; Testnet reconciliation henüz `none` |
| C3 | H-ENV ve alarm tatbikatı | Tatbikat **PASS** (iki koşu). H-ENV: ilk koşu eksikti; düzeltmeden sonraki tam koşu **78 satır, 0 hata** (§3) |
| C4 | Runbook provası (yedek/geri yükleme), mutabakat, tarayıcı kontrolü | Tarayıcı kontrolü tamamlandı. Yedek: kullanıcı kararıyla M5 kanıtı kabul edildi (§4). 2026-10 fatura mutabakatı açık |
| C5 | Kabul | Bu belge (U, 2026-11-01) |

## 2. Alarm tatbikatı (`m10-alarm-drill.ps1`, kullanıcı)
**Tam koşu** (rapor `m10-alarm-drill-20261101T000757Z.json`, SHA-256 `da035d6838d3cc63bee19da87204ea3bcda68933d763ba7db76a1df70bcf0745`, `pass=True`):

| Adım | Sonuç | Operasyonel alanlar |
|---|---|---|
| Trading kill switch (A1) | PASS | alarm görüldü ve temizlendi; Testnet açık; pozisyon 0, kabul edilmiş stop 0 (önce ve sırasında), açık giriş emri 0, korumasız 0 |
| AI kill switch (A2) | PASS | alarm görüldü ve temizlendi; `clear` çıkış kodu 0; manuel tetik A4 üretmedi |
| Bütçe (A3) | PASS | 2026-11: 0 / 10 USD, beklenen ve gerçek alarm yok |
| H-ENV (A5) | PASS | A5 yok (aynı gün `henv_run_recorded` PASS kaydı; §3) |
| Heartbeat (A6) | PASS | alarm yok |

**İkinci koşu** (`-SkipTradingKillSwitch -SkipAiKillSwitch`; `m10-alarm-drill-20261101T000923Z.json`, SHA-256 `8fbb3c0c5fbbe254987aa1053f45e9da1090c67a1f0e6c403300aebe9d92ab4f`): `pass=True`, kritik 0, uyarı 0.

## 3. H-ENV aylık koşusu
- Rapor `m5-henv-20261101T000640Z.txt`, SHA-256 `018cc23389909dacc45f11a100d21c3b9efe2249ad10ff5805a7a1beb2504fef`: **58 satır, 58 PASS**; ops'a `henv_run_recorded` (PASS) yazıldı ve A5 temizlendi.
- **Koşu eksiktir.** `holdout-evaluator` container'ı açık kaldığı için üç probe `Address already in use` ile çalışmadı. Çalışmayan probe hiç satır yazmadığından rapor 0 hata gösterdi. M5'teki tam koşu 79 satırdı (`-Cohort` ile, H22 dahil). Eksik satırların hangi H kimliklerine ait olduğu Claude Code tarafından incelenmedi (rapor içeriği okunmadı).
- **Sonuç:** bu koşu aylık H-ENV kanıtı sayılmaz. `HoldoutEnvironmentVerified` bayrağı değişmedi (evaluator digest'i aynı); bu koşuyla yeniden set edilmemelidir.
- **Tam koşu (aylık kanıt, kullanıcı, 2026-11-01; `holdout-evaluator` durdurulduktan sonra, §5 düzeltmesiyle):** Tam koşu `m5-henv-20261101T002326Z.txt`, SHA-256 `a71b3da30fad777a3127069985838ac09c84b60a235fdbffe93086b2f16ebf58`: **78 satır, 0 hata**; ops'a `henv_run_recorded` (PASS) yazıldı.
  - Çalışan kimlikler: H5, H6, H6b, H9, H10, H14, H15, H16, H19, H20, H21, H23, H24, H25, H26, H27, H28. Bu liste zorunlu kimliklerin hepsini içerir.
  - Koşu `-Cohort`'suz yapıldı, bu yüzden H22 yok. M5'teki 79 satırlık koşu ile fark bu tek satırdır.
  - Bir sonraki aylık koşu en geç 2026-12-02'de gelir (A5).

## 4. Kabul kriterleri (roadmap M10)
| Kriter | Kanıt | Sonuç |
|---|---|---|
| Alarmlar test edildi (trading kill switch, LLM acil durdurma, H-ENV aylık koşusu, bütçe) | Çevrimdışı: `AlarmTests` (A1–A7 sınır değerleri, `ops_api` okuması, rota), `DashboardTests` (bütçe %85 → A3 uyarısı), mevcut `Ai.Tests` otomatik tetik testleri. Gerçek ortam: tatbikat A1 ve A2 PASS, A3 ve A6 durum kontrolü; A5 mekanizması gerçek kayıtla temizlendi | ✅ Bütçe alarmı yalnızca çevrimdışı (protokolde kabul edildi). H-ENV koşusunun kendisi eksik (§3) |
| Runbook: bant dışı anahtar iptali, kapanma/yeniden başlatma sonrası devam, Docker Desktop bellek ayarı | [`runbook.md`](runbook.md) §1–§8: alarm → eylem, Binance Testnet ve Google Cloud anahtar iptali/yenilemesi, uyku/yeniden başlatma ve catch-up, `.wslconfig`, aylık takvim (H-ENV, yedek, geri yükleme, mutabakat), Testnet sıfırlaması ve rebaseline, prova kontrol listesi | ✅ |
| 30 günlük Testnet provası tamamlandı | 2026-11-01'de başladı (C2). Bitişi en erken ~2026-12-01 | ⏳ **Kabul edilen sapma** (U-M9-5, D35) |

- **Yedek ve geri yükleme (C4):** bu Kapı C'de yeni yedek ya da geri yükleme yapılmadı. Kullanıcı kararıyla M5 kanıtı (`ops-20261020T151039Z.dump.enc`, 33/33 tablo eşleşti) ve runbook §5 prosedürü kabul edildi. Bir sonraki aylık takvimde yeni yedek ve geri yükleme testi önerilir.

## 5. Kapanış düzeltmesi: H-ENV eksik koşuyu artık PASS saymaz
- **Hata:** `m5-henv.ps1` yalnızca probe'ların yazdığı `Hxx PASS|FAIL` satırlarını sayıyordu. Çalışamayan bir probe satır yazmadığı için koşu "0 hata" görünüyordu. M10 ile bu sonuç ops'a PASS olarak yazılıp A5'i temizliyordu.
- **Düzeltme:**
  - Her probe çağrısından sonra çıkış kodu kontrol edilir; sıfır değilse `HENV FAIL probe-<ad>-ran` satırı yazılır.
  - Zorunlu kimliklerin (H5, H6, H6b, H14, H19–H21, H23–H28; `-Cohort` ile H22) her birinin en az bir satırı olmalıdır; yoksa `HENV FAIL missing-<id>` satırı yazılır.
  - Böylece eksik koşu FAIL kaydedilir ve A5 kritik olur.
  - `EnvironmentConfigurationTests` bunu sabitler.
  - Adında `Holdout` geçen dosya ve evaluator kodu değişmedi.
- **Not:** probe'ların başarıda 0 ile çıktığı varsayılır (M5 koşusundaki davranış). Aksi görülürse bir sonraki koşu bunu `probe-…-ran` satırıyla gösterir.

## 6. Açık kalanlar (kullanıcı)
1. ~~H-ENV'in yeniden koşulması~~: **tamamlandı** (2026-11-01, 78 satır, 0 hata; §3).
2. **İlk Testnet reconciliation kaydı** ve ilk gerçek strateji emri: `m6-update.ps1 -ChecksOnly` ile temiz görülmeli; ilk emirden sonra dashboard'da zincir kontrolü (M9 §4).
3. **2026-10 AI fatura mutabakatı:** `scripts/m4-ai.ps1 reconcile --month 2026-10 --invoice-try <TL> --usd-try <kur>`.
4. **30 günlük prova sonu** (≥ 2026-12-01): döngüler, kesintiler, reconciliation, emir yaşam döngüsü sayıları, sıfırlama/rebaseline, alarm geçmişi ve `docker stats` belleği bu rapora ek olarak işlenir (runbook §7). `requirements.md` §8'deki bellek TBD'si bu ölçümle kapanır.
5. ~~Kohort 1'in Testnet terfisi~~: **yapıldı** (U, 2026-11-01; §7).

## 7. Ek — Kohort 1'in Testnet terfisi (U, 2026-11-01)
- **Karar (kullanıcı):** D35'teki "yalnızca Kohort 0" kapsamı kullanıcı kararıyla genişletildi. Kohort 1'in üç stratejisi `h08-smabtc-n150-b300`, `h04-macd-f12-s26-g12` ve `h07-volbo-n50-v2p5` `m6-testnet-activate.ps1` ile `TestnetActive` yapıldı.
- **D30 (terfi anı, kullanıcı çıktısı):** `days=10,24/10`, `bars=61/60`, `criticalErrors7d=0`, `eligible=True`.
- **Durum:** lifecycle `TestnetActive=5`. Beş strateji Testnet'te; paper run'ları gölge olarak paralel sürer (U-M6-4).
- **Sermaye (`m6-protocol.md` v1.1 §13, A-4):** Testnet run'ı kohort başına 5.000 USDT nominaldir. İki kohortun en fazla maruziyeti yaklaşık 8.000 USDT'dir ve hesabın yaklaşık 10.000 USDT bakiyesine sığar.
- **Kontrol edilecek:** Kohort 1'in Testnet run'ı bir sonraki döngüde açılır. Bundan sonraki `m6-update.ps1 -ChecksOnly` çıktısında açık run 2, bilinmeyen emir 0, korumasız pozisyon 0 ve temiz reconciliation görülmelidir. Bu kontrol 30 günlük prova gözlemine dahildir.
- 30 günlük prova Kohort 0 için 2026-11-01'de, Kohort 1 için terfisiyle başladı.
