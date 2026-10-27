# M6 Kabul Raporu — Binance Spot Testnet execution

- Durum: **Kapı C sürüyor.** C1 ve C2 tamamlandı; A-4 v1.1 ile güncellendi (§3); C3 (`m6-update.ps1`), C4 (terfi, D30) ve C5 (kabul) bekliyor.
- Ön kayıt: [`m6-protocol.md`](m6-protocol.md) v1 (`b2433bc`). ADR-0021. Kapı B: `a19a5e4` (`verify.ps1` yeşil, 598 test).
- Claude Code bu rapora yalnızca operasyonel alanları yazar (m6-protocol §8.3). Fiyat, miktar, bakiye tutarı ve PnL içermez.

## 1. Kapı C adımları
| # | Adım | Durum |
|---|---|---|
| C1 | Testnet HMAC anahtarı, işlem izni açık (`m6-testnet-key.ps1`) | Tamamlandı (2026-10-27, kullanıcı) |
| C2 | Tatbikat, ≥ 20 emir (`m6-testnet-drill.ps1`) | **PASS** (2026-10-27T22:35Z) |
| C3 | Migration 0009, Trading.Host yeniden derleme, Testnet etkin (`m6-update.ps1`) | Bekliyor |
| C4 | `PaperTrading → TestnetActive` (D30 dolunca), ilk Testnet döngüleri, kill switch denemesi | Bekliyor: en erken Kohort 0 için 2026-11-03, Kohort 1 için 2026-11-04 |
| C5 | Kabul | Bekliyor |

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
