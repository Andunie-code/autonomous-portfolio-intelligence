# Operasyon Kılavuzu (Runbook)

- Durum: M10 (`m9-m10-protocol.md` §4; ADR-0024). Son güncelleme: 2026-11-01.
- Kapsam: tek yerel makinede çalışan sistem (ADR-0013): PostgreSQL, Trading.Host (paper + Binance Spot Testnet), iş bazlı Research.Host ve HoldoutEvaluator.
- Bu kılavuzdaki script'lerin tamamı **kullanıcı** tarafından, proje kökünde çalıştırılır: `powershell -ExecutionPolicy Bypass -File scripts/<ad>.ps1`. Claude Code bu script'leri çalıştırmaz (CLAUDE.md, T17).
- Raporlar `%LOCALAPPDATA%\PortfolioIntelligence\reports` altındadır. Paylaşılırken yalnızca operasyonel alanlar (sayılar, durumlar, zamanlar, hash'ler) paylaşılır; fiyat, bakiye ve PnL paylaşılmaz.
- Dashboard: `http://127.0.0.1:55490/dashboard` (Ops API token'ı ile). Her sekmenin üstündeki alarm şeridi etkin alarmları gösterir; ayrıntı "Sistem ve AI" sekmesindedir.

## 0. Hızlı başvuru
| Durum | İlk komut |
|---|---|
| Genel sağlık | `scripts/m6-update.ps1 -ChecksOnly` |
| Alarmlar | Dashboard alarm şeridi; `scripts/m10-alarm-drill.ps1` (tatbikat) |
| Her şeyi durdur (yeni giriş yok) | Ops API `POST /kill-switch {engaged: true}` (aşağıda §1 A1) |
| LLM'i durdur | `scripts/m4-ai.ps1 killswitch trip --reason "..."` |
| Makine açıldıktan sonra | §3 |
| Testnet sıfırlandı | §6 |

## 1. Alarm → eylem tablosu
Alarmlar her istekte mevcut kayıtlardan hesaplanır (ADR-0024); saklanmaz, susturulamaz ve dışarı gönderilmez. Koşul ortadan kalkınca alarm kendiliğinden kaybolur.

| Kod | Önem | Anlamı | Eylem |
|---|---|---|---|
| A1 `trading-kill-switch` | kritik | Trading kill switch açık (kullanıcı ya da HTTP 418 sonrası `system`). Yeni giriş yok; stop'lar ve çıkışlar sürer | Nedeni `/system`'de (aktör, neden). HTTP 418 ise **en az 1 saat bekleyin**, IP yasağının kalktığını doğrulayın. Kapatma: `Invoke-RestMethod -Method Post http://127.0.0.1:55490/kill-switch -Headers @{Authorization="Bearer <token>"} -ContentType application/json -Body '{"engaged":false,"reason":"..."}'` |
| A2 `ai-kill-switch` | kritik | LLM acil durdurma açık. Allocator T11'e düşer; trading sürer | Tetik türüne bakın. `MonthlyCap` ise ay dönene kadar açık kalabilir. Sorun giderildiyse: `scripts/m4-ai.ps1 killswitch clear --reason "..."` (etkileşimli, `EVET`) |
| A3 `ai-budget` | uyarı / kritik | Bu ayın harcaması (kesinleşen + açık rezervasyon) tavanın %80'i / %100'ü | Dashboard'da görev sınıfı ve iş dökümüne bakın. Beklenmedik ise A2'yi elle açın ve §2.2'yi uygulayın |
| A4 `ai-auto-trip` | uyarı | Son 7 günde otomatik AI durdurması (hız, maliyet sapması, circuit breaker, aylık tavan) | `scripts/m4-ai.ps1 ledger --month <YYYY-MM>`; sapma sürüyorsa anahtarı iptal edin (§2.2) |
| A5 `henv-stale` | uyarı / kritik | Son başarılı H-ENV koşusu 31 günden eski ya da hiç yok (uyarı); son koşu FAIL (kritik) | §5'teki aylık H-ENV koşusu. FAIL ise bayrak set edilmez; nedeni giderip yeniden koşun |
| A6 `heartbeat` | kritik | 75 dakikadır döngü yok: makine, Docker Desktop ya da Trading.Host kapalı | §3 |
| A7 `unprotected-position` | kritik | Bir Testnet pozisyonunun borsada kabul edilmiş stop emri yok; yeni girişler bloke | Sistem stop'u her döngüde yeniden dener. İki döngüden uzun sürerse `m6-update.ps1 -ChecksOnly`; gerekirse `POST /positions/close` ile pozisyonu kapatın |
| A7 `order-unknown` | kritik | Bir emir 60 dakikadan uzun `Unknown`/`Submitting` | Sistem `clientOrderId` ile sorgulamayı sürdürür; körü körüne yeniden gönderilmez. Testnet erişimini (internet, `testnet.binance.vision` durumu) kontrol edin; `m6-update.ps1 -ChecksOnly` |
| A7 `testnet-reset-suspected` | kritik | Testnet sıfırlandı gibi: açık emirler kayıp, bakiye tabandan sapmış | §6 |
| A7 `reconciliation` | kritik | Son 7 günde başarısız reconciliation (defter ↔ bakiye/durum) | Yeni giriş zaten bloke. `m5-paper-report.ps1` ve `m6-update.ps1 -ChecksOnly` çıktısıyla inceleyin; neden anlaşılmadan kill switch kapatılmaz. Bu, D30 kritik hatasıdır |
| A7 `strategy-hash-mismatch` | kritik | Strateji tanımı manifesto hash'iyle uyuşmuyor; strateji çalıştırılmıyor (V14) | Kod ya da registry değişikliğini inceleyin. Dondurulmuş kayıtlar değiştirilmez; düzeltme yeni sürümle yapılır |
| A7 `cycle-error` | uyarı | Son 24 saatte hata ile biten döngü | Audit'teki `cycle_error` olayı (tür ve zaman). Tekrarlıyorsa `docker compose logs trading-host` |

## 2. Bant dışı API anahtarı iptali ve yenilemesi
Kodumuzdan bağımsız son çare. Anahtar dosyaları yalnızca `secrets/` altındadır ve Trading.Host başlarken okunur; değişiklik ancak Trading.Host yeniden oluşturulunca (`--force-recreate`) etkili olur.

### 2.1 Binance Spot Testnet
1. Önce yeni girişleri durdurun: A1 için kill switch'i açın.
2. `https://testnet.binance.vision` → GitHub ile giriş → API anahtarları → ilgili HMAC anahtarını **silin**. Borsa anahtarı reddettiği anda Testnet emirleri hata alır; stop'lar borsada kalır.
3. Yerel dosyayı boşaltın: `scripts/m6-testnet-key.ps1 -Clear`, ardından `scripts/m6-update.ps1` (Trading.Host Testnet'siz yeniden başlar; paper sürer).
4. **Yenileme:** yeni HMAC anahtarı oluşturun, **işlem iznini açın** (yeni anahtarlar varsayılan olarak işlem yapamaz, S-B11), IP kısıtı koymayın (D33). `scripts/m6-testnet-key.ps1` (gizli giriş) → `scripts/m6-update.ps1` → `-ChecksOnly` temiz → kill switch'i kapatın.
5. Testnet'te çekim yoktur; anahtar sızıntısının etkisi sanal bakiyeyle sınırlıdır.

### 2.2 Google Cloud / Gemini
1. `scripts/m4-ai.ps1 killswitch trip --reason "anahtar iptali"` (yeni çağrı yapılmaz; allocator T11'e düşer).
2. Google Cloud Console → ilgili proje → **APIs & Services → Credentials** → runtime anahtarını **Delete** (ya da **Regenerate key**). Geliştirme anahtarı ayrı projededir; gerekiyorsa onu da aynı yoldan iptal edin.
3. İsteğe bağlı: **Billing → Budgets & alerts** üzerinden proje bütçesini düşürün; sağlayıcı tarafı limitler ikinci katmandır (architecture §3.4).
4. Yerel dosyayı boşaltın: `scripts/m8-update.ps1 -ClearKey` (Trading.Host yeniden oluşturulur; her allocator kararı T11).
5. **Yenileme:** yeni runtime anahtarı → `scripts/m8-update.ps1 -SetKey` → `scripts/m4-ai.ps1 killswitch clear --reason "..."`.
6. Ay sonunda §5.4 mutabakatında iptal öncesi harcamayı kontrol edin.

## 3. Uyku, kapanma ve yeniden başlatma sonrası devam
**Beklenen davranış (ADR-0013, risk-model §4):**
- Makine kapalıyken paper döngüsü durur. Testnet'te koruyucu stop emirleri borsada bekler ve tetiklenebilir.
- Trading.Host açılışta **ilk döngüyü hemen** çalıştırır: önce reconciliation ve borsa bakımı (Testnet'te tetiklenmiş stop'lar dolum olarak alınır), sonra kaçırılan kapanmış 4h mumlar **sırayla** işlenir (catch-up; stop kuralı dahil). Sonraki döngüler her saat HH:00:15 UTC'dedir.
- Kesinti, D30 ve paper raporunda "kesinti oranı" ve geç işlenen mum olarak görünür; ileriye dönük değerlendirmeyi etkilemez.

**Adımlar:**
1. Docker Desktop'ın çalıştığından emin olun. Önerilen ayar: *Settings → General → Start Docker Desktop when you sign in to your computer*. Compose'da `trading-host` için `restart: unless-stopped` vardır; Docker Desktop açılınca kendiliğinden kalkar (elle `docker compose stop` yapılmadıysa). `postgres` servisinde yeniden başlatma politikası yoktur; Trading.Host veritabanına bağlanamazsa adım 2'yi uygulayın.
2. Kalkmadıysa: `scripts/m5-paper-start.ps1` (Trading.Host'u `--force-recreate` ile başlatır).
3. İlk döngüden sonra (en geç bir sonraki HH:00:15 UTC): `scripts/m6-update.ps1 -ChecksOnly`. Beklenen: reconciliation temiz, `Unknown` 0, korumasız pozisyon 0. A6 kaybolmuş olmalıdır.
4. Uzun kesintiden sonra Testnet'te `testnet-reset-suspected` görülürse §6.
5. Planlı güncellemeden (Windows Update) önce açık Testnet pozisyonlarının stop'larının borsada olduğunu `-ChecksOnly` ile doğrulayın; ayrıca bir şey yapılması gerekmez.

## 4. Docker Desktop (WSL2) bellek ayarı
- Servis sınırları Compose'dadır (`mem_limit`, `cpus`; ADR-0013 §3): PostgreSQL 1 GB / 1.0, Trading.Host 384 MB / 0.5, Research.Host 1,5 GB / 2.0, HoldoutEvaluator 1,5 GB / 2.0. Research.Host ve evaluator iş bazlıdır ve aynı anda ağır iş çalıştırmaz.
- WSL2'nin toplam tavanı `%UserProfile%\.wslconfig` ile ayarlanır. 16 GB RAM'li makine için öneri (Windows ve IDE için ≥ 8 GB bırakılır):

  ```ini
  [wsl2]
  memory=6GB
  processors=4
  swap=2GB
  ```

- Uygulamak için: Docker Desktop'ı kapatın, PowerShell'de `wsl --shutdown`, Docker Desktop'ı yeniden açın, ardından §3 adım 3.
- **Ölçüm (30 günlük prova sırasında; `requirements.md` §8 TBD):** `docker stats --no-stream` çıktısındaki `MEM USAGE / LIMIT` sütunu (yalnızca servis adları ve bellek değerleri paylaşılır). Trading.Host'un 384 MiB'a, PostgreSQL'in 1 GiB'a yaklaşması (> %80) sınırların gözden geçirilmesi gerektiğini gösterir.

## 5. Aylık takvim
| Ne zaman | İş | Komut | Beklenen |
|---|---|---|---|
| Ayda bir (A5 en geç 31. günde uyarır) | H-ENV | `scripts/m5-henv.ps1` | Tüm satırlar PASS; ops'a `henv_run_recorded` yazılır, A5 kaybolur. Evaluator imajı değiştiyse bayrak için çıktıdaki `henv-mark-verified` komutu |
| Haftada bir (öneri), ayda en az bir | Şifreli yedek | `scripts/m5-backup.ps1` | `PI_BACKUP_DIR` altında `ops-*.dump.enc` ve kayıt dosyaları; **harici diske kopyalayın** |
| Ayda bir | Geri yükleme testi | `scripts/m5-restore-test.ps1 -Backup <ops-*.dump.enc>` | Tablo başına sayı ve hash eşleşmesi; yedekten sonra yazılan satırlar raporda ayrıca belirtilir |
| Ay başı (fatura kapanınca) | AI fatura mutabakatı | `scripts/m4-ai.ps1 reconcile --month <YYYY-MM> --invoice-try <TL> --usd-try <kur>` | Fark ≤ %10. Aşarsa fiyat tablosu (`ai.v1.json`, `sources.md`) gözden geçirilir; olay audit'e yazılır, çıkış kodu 3 |
| Yaklaşık ayda bir, habersiz | Testnet sıfırlaması | §6 | Yeni Testnet run'ı ve taban |

### 5.1 H-ENV ayrıntısı
- H-ENV, Research ve Trading konumlarından holdout'a erişilemediğini sınar (`testing-strategy.md` §7.2). Trading.Host çalışırken koşulur (H23, H24).
- Son koşu FAIL ise `HoldoutEnvironmentVerified` set edilmez; A5 kritik görünür.

### 5.2 Yedek ayrıntısı
- Hedef klasör mutlak yol olmalı; repo dizini ve `OneDrive` altı reddedilir. Anahtar `secrets/backup_key`'tir; anahtar kaybolursa yedek açılamaz, anahtarın kendisini de ayrı ve güvenli bir yerde saklayın.
- Günlük çalıştırma istenirse Windows Görev Zamanlayıcı'ya `powershell -ExecutionPolicy Bypass -File <kök>\scripts\m5-backup.ps1` eklenebilir.

## 6. Testnet aylık sıfırlaması ve `rebaseline`
- **Belirti:** A7 `testnet-reset-suspected`; reconciliation açık emirlerin tamamını kayıp ve bakiyeyi tabandan sapmış bulur; yeni girişler bloke olur. Paper etkilenmez.
- **Adımlar:**
  1. Testnet sitesinde bakiyenin varsayılana döndüğünü ve API anahtarının hâlâ geçerli olduğunu görün (sıfırlama anahtarları korur).
  2. `scripts/m6-testnet-activate.ps1 -Rebaseline -Reason "Testnet aylık sıfırlaması"` (etkileşimli onay).
  3. Bir sonraki döngüde eski Testnet run'ı `reset-lost` notuyla kapanır, aynı sleeve yapısıyla yeni run ve yeni bakiye tabanı açılır.
  4. `scripts/m6-update.ps1 -ChecksOnly`: yeni run açık, reconciliation temiz, A7 kaybolmuş.
- Kayıp pozisyonlar L3 sapma raporunda "kesinti" olarak görünür.

## 7. 30 günlük Testnet provası kontrol listesi (M10)
Başlangıç: Kohort 0'ın `TestnetActive` olduğu ve ilk Testnet döngüsünün temiz göründüğü an (`m9-m10-protocol.md` §5).

| Sıklık | Kontrol |
|---|---|
| Her gün | Dashboard alarm şeridi boş ya da yalnızca bilinen uyarılar; Testnet sekmesinde `Unknown` 0, korumasız pozisyon 0 |
| Haftada bir | `scripts/m6-update.ps1 -ChecksOnly` ve `scripts/m5-paper-report.ps1` (operasyonel alanlar); `docker stats --no-stream` belleği |
| Sıfırlamada | §6 |
| Sonda (≥ 30 gün) | Döngü sayısı ve sonuçları, kesintiler, reconciliation, emir yaşam döngüsü sayıları, sıfırlama/rebaseline, alarm geçmişi ve bellek `m10-acceptance-report.md` ekine işlenir |

## 8. Alarm tatbikatı
- `scripts/m10-alarm-drill.ps1`: trading kill switch'i açıp kapatır (A1; Testnet giriş emirleri iptal, stop'lar kalır), AI kill switch'i açıp kapatır (A2; `EVET`), bütçe ve H-ENV durumunu yazar. Zaten açık olan bir switch'e dokunmaz.
- Çıktı: rapor yolu, SHA-256 ve `pass=True|False`. Tatbikat aylık takvimle birlikte ya da büyük bir güncellemeden sonra tekrarlanabilir.
