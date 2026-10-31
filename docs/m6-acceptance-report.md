# M6 Kabul Raporu — Binance Spot Testnet execution

- Durum: **M6 tamamlandı (kullanıcı kabulü, U, 2026-10-28).**
  - C1, C2 ve C3 tamamlandı.
  - **Kabul edilen sapma (U):** C4 (gerçek `PaperTrading → TestnetActive` terfisi ve ilk Testnet döngüleri), D30 dolduktan sonra yapılacak operasyonel bir adım olarak M6 kapanışından sonraya bırakıldı (§5). Bu, M5'teki 7 günlük paper gözlemiyle aynı biçimde ele alındı.
- Ön kayıt: [`m6-protocol.md`](m6-protocol.md) v1 (`b2433bc`) ve v1.1 (`86f5f1b`, A-4). ADR-0021. Kapı B: `a19a5e4` (`verify.ps1` yeşil, 598 test; v1.1 ile 599).
- Claude Code bu rapora yalnızca operasyonel alanları yazar (m6-protocol §8.3). Fiyat, miktar, bakiye tutarı ve PnL içermez.

## 1. Kapı C adımları
| # | Adım | Durum |
|---|---|---|
| C1 | Testnet HMAC anahtarı, işlem izni açık (`m6-testnet-key.ps1`) | Tamamlandı (2026-10-27, kullanıcı) |
| C2 | Tatbikat, ≥ 20 emir (`m6-testnet-drill.ps1`) | **PASS** (2026-10-27T22:35Z) |
| C3 | Migration 0009, Trading.Host yeniden derleme, Testnet etkin (`m6-update.ps1`) | **Tamamlandı** (2026-10-27T22:51Z, kullanıcı; §4) |
| C4 | `PaperTrading → TestnetActive` (D30 dolunca), ilk Testnet döngüleri, kill switch denemesi | **Kapanıştan sonraki operasyonel adım** (§5): en erken Kohort 0 için 2026-11-03, Kohort 1 için 2026-11-04 |
| C5 | Kabul | **Tamamlandı** (kullanıcı kabulü, 2026-10-28; §6) |

**Script düzeltmeleri (C2 öncesi, CHANGELOG 2026-10-28):**
- `4c05817`: secret dosyalarının izinleri; tatbikat imajının derlenmesi.
- `0111884`: statik IP çakışmasında çalışan Trading.Host'un `stop`/`start` ile duraklatılması.

## 2. C2 — tatbikat
- **Rapor:** `m6-testnet-drill-20261027T223535Z.json` (repo dışında), SHA-256 `0d82ad11957bdd7696d6896244ffe23e21ef0247291c50f56474978822472ccc`.
- **Sonuç:** `pass=true`.
  - Emir: 24 gönderim; terminal duruma ulaşan (tam yaşam döngüsü) **23**; duplicate 0.
  - Borsada `-2010` ile reddedilen ikinci gönderim, açık emirle aynı kimliği taşıyan bilinçli denemedir.
- **Senaryolar (hepsi PASS):**
  - limit GTC ve iptal;
  - duplicate `clientOrderId`;
  - yerel filtre reddi: 2 emir, ağa istek gitmedi;
  - 4 tur giriş → stop → cancel-replace → çıkış;
  - `Unknown` kurtarma;
  - kill switch (giriş iptal, stop korundu);
  - düz kapanış ve bakiye mutabakatı.
- **`Unknown` kurtarma:** 1 enjekte edildi, 1 çözüldü. İstek borsaya ulaşmamıştı; `recvWindow` + 60 sn ufkundan sonra `NeverAccepted` oldu (m6-protocol §3.3, gerçek borsada).
- **Borsa özellikleri:**
  - `STOP_LOSS` desteği 20/20 sembolde var;
  - `canTrade=true`;
  - saat ofseti 433 ms;
  - gecikme medyanı 264 ms, p95 503 ms.
- **`capitalSufficient=false`:** tatbikat başında serbest USDT < 20.000 (iki kohort × 10.000 nominal). Karar: §3 (v1.1, 5.000 USDT).

**Roadmap kriteri "Testnet'te ≥ 20 emrin tam yaşam döngüsü tamamlandı"** bu raporla karşılandı (23 ≥ 20).

## 3. Karar: Testnet sermayesi (A-4 → v1.1; U, 2026-10-28)
- **Sorun:** Testnet hesabının USDT bakiyesi (kullanıcı beyanı: yaklaşık 10.000, aylık sıfırlamada yeniden bu değere döner), kohort başına 10.000 nominalle iki kohortu karşılamıyordu (en fazla maruziyet yaklaşık 16.000).
- **Karar:** kullanıcı seçenek A'yı seçti: Testnet run sermayesi kohort başına **5.000 USDT** (m6-protocol §13). İki kohortun en fazla maruziyeti yaklaşık 8.000 USDT'dir.
- **Değerlendirilip seçilmeyenler:**
  - B: A-4 aynı kalsın; önce yalnızca Kohort 0 terfi etsin;
  - C: Testnet'teki sanal varlıkları USDT'ye çevirmek (her sıfırlamada tekrar gerekirdi).

## 4. C3 — güncelleme (`m6-update.ps1`, kullanıcı, 2026-10-27T22:51Z)
- **Güncelleme:** migration 0009 uygulandı. Trading.Host M6 koduyla, canlı veri ve Testnet açık olarak yeniden başladı. Yeniden başlatmadan sonraki ilk döngü `ok` (degraded yok).
- **Kontroller:**
  - Testnet etkin; açık Testnet run'ı 0; `Unknown` emir 0; korumasız pozisyon 0; outbox boş. Henüz `TestnetActive` strateji yok, bu yüzden borsaya emir gitmedi.
  - Kill switch kapalı.
  - Lifecycle satırı script hatası yüzünden tek grup halinde yazıldı (Windows PowerShell 5.1 dizi sarmalaması). Düzeltildi (§6); beklenen gerçek durum 5 strateji `PaperTrading`'dir ve aşağıdaki D30 satırları da bunu gösteriyor.
- **D30 ilerlemesi (C3 anı):**

  | Strateji | Gün | Mum | Kritik hata (7 gün) |
  |---|---|---|---|
  | `ema-trend`, `supertrend` (Kohort 0) | 7,33/14 | 43/60 | 0 |
  | `h08`, `h04`, `h07` (Kohort 1) | 6,17/14 | 36/60 | 0 |
- **Operasyonel paper raporu** (SHA-256 `82fa39d474b50e42396821aee1263198b35dcc80d88111772a07c3d2435b9224`):
  - 56 döngü; gecikmeli döngü (> 60 sn) 0; sonuçlar `ok:48, degraded:6, error:2`. İki `error` döngüsü, migration 0008'den öncekilerdir.
  - Döngü süresi medyanı 387 ms, p95 9.298 ms, en fazla 11.369 ms.
  - 624 mum işlendi; catch-up döngüsü 6; bayat döngü 5; kapanıştan 5 dakikadan geç işlenen mum 71. Geç işlenen mumlar, makinenin kapalı olduğu sürelerden sonra sırayla işlenenlerdir.
  - Kesintiler: 2026-10-20T23:00Z–10-21T09:48Z, 2026-10-21T23:00Z–10-26T18:46Z, 2026-10-26T22:00Z–10-27T17:45Z.
  - Reconciliation 75 koşu, 0 tutarsızlık; duplicate dolum 0; negatif nakit 0; canlı mum anomalisi 0.
  - Audit: kill switch bir kez açılıp kapatıldı; `risk_config_version_added` 1; `paper_run_created` 2; `cycle_error` 2.
  - Bellek: Trading.Host 54,92 MiB / 384 MiB; PostgreSQL 62,18 MiB / 1 GiB.

## 5. C4 — kapanıştan sonraki operasyonel adım (kabul edilen sapma)
- **Ne zaman:** D30 dolunca. En erken Kohort 0 için 2026-11-03, Kohort 1 için 2026-11-04. 60 mum şartı ve son 7 günde 0 kritik hata şartı ayrıca aranır; `m6-update.ps1 -ChecksOnly` ile izlenir.
- **Adımlar (kullanıcı):**
  1. `m6-testnet-activate.ps1 -Strategy <ad> -Reason "..."`;
  2. ilk Testnet döngülerinden sonra `m6-update.ps1 -ChecksOnly`: korumasız pozisyon 0, takılı `Unknown` 0, reconciliation temiz;
  3. Testnet run'ında bir kill switch aç/kapat denemesi (stop'lar borsada kalmalı).
- **Kayıt:** sonuçlar bu raporun eki olarak ve CHANGELOG'a işlenir.
- **Aylık Testnet sıfırlaması** (S-B19, S-B24): `testnet-reset-suspected` görülürse `m6-testnet-activate.ps1 -Rebaseline`.
- **D30 rev. (U, 2026-10-30; `m6-protocol.md` v1.2 §14):** gün şartı 14'ten 10 takvim gününe indi; 60 mum ve sıfır kritik hata şartları değişmedi. Kohort 0 (`ema-trend`, `supertrend`) kullanıcının çıktısında `days=10,32`, `bars=61/60`, `criticalErrors7d=0`. C4 yalnızca Kohort 0 için, M9–M10 Kapı C'sinde (C1–C3) yapılır (`m9-m10-protocol.md` §6); Kohort 1'in terfisi ayrı kullanıcı kararıdır.

## 6. Kabul (roadmap M6 kriterleri)
| Kriter | Kanıt | Durum |
|---|---|---|
| Giriş dolumundan sonra borsaya koruyucu stop | Kapı B: uçtan uca testler (her holding'in stop'u); C2: 4 tur + kill switch senaryosu, gerçek Testnet | Karşılandı |
| Kill switch giriş emirlerini iptal eder, stop'ları korur (D11 rev.) | Kapı B: executor ve uçtan uca testler; C2 senaryosu, gerçek Testnet | Karşılandı |
| Stop emri reddi alarm üretir ve yeni girişleri durdurur | Kapı B: korumasız stop akışı testi (`protective_stop_failed`, `unprotected` blokajı, yeniden yerleştirme) | Karşılandı (çevrimdışı) |
| Testnet stop sapmaları fark raporunda kategorize edilir | `/testnet` L3 sapma kategorileri (`TestnetDeviations`); Kapı B testleri | Mekanizma karşılandı; gerçek strateji stop'larının verisi C4'ten sonra gelir |
| Fault injection altında duplicate emir 0 ve ledger reconcile | Kapı B: fault injection matrisi, restart, uçtan uca; C2: duplicate 0 ve `Unknown` → `NeverAccepted`, gerçek Testnet | Karşılandı |
| Testnet'te ≥ 20 emrin tam yaşam döngüsü | C2: 23 | Karşılandı |
| Filtre ihlali yapan emir borsaya gitmeden reddedilir | Kapı B: filtre property testleri; C2: 2 yerel ret, istek yok | Karşılandı |
| Anahtar yalnızca Trading.Host'ta | Compose ve mimari testleri (`TestnetIsolationTests`); C1 ve C3 | Karşılandı |
| `PaperTrading → TestnetActive` yalnızca `Frozen` soyundaki stratejiler için ve kullanıcı oturumuyla; doğrulama gerektirmez; `experimental` etiketi | Kapı B: V10 testleri (401/400/409, DB trigger'ı, roller); gerçek geçiş C4'te | Karşılandı (çevrimdışı); gerçek geçiş kabul edilen sapma |

- **Kapanış sonrası düzeltme:** `m6-update.ps1`'in lifecycle sayımı, Windows PowerShell 5.1'in JSON dizisini tek nesne döndürmesi yüzünden tüm durumları tek grupta topluyordu. Dizi artık pipeline ile açılıyor (M5b'deki `m5-cohort1-activate.ps1` düzeltmesiyle aynı).
