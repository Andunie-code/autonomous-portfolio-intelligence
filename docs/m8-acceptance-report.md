# M8 Kabul Raporu — Portfolio Allocator ve gölge mod

- Durum: **M8 tamamlandı (kullanıcı kabulü, U, 2026-10-31; kabul edilen sapma, U-M8-4).**
  - Kapı A (`d1acd2b`), Kapı B (`5b36c5b`, 770 test), v1.1 ve pilot protokolünün onayı (`c739091`), C0 düzeltmesi (`e5bf632`), C0–C2 kullanıcı koşuları tamamlandı.
  - **Sonuç:** allocator **gölge modda** çalışıyor. Gerçek emirler T11 (`allocation v1`) ile verilmeye devam ediyor. 14 günlük pilot, yeterlilik protokolü (C3) ve yeterlilik penceresi kapanıştan sonra **arka planda süren operasyonel gözlem** olarak devam eder. Bunların sonuçları bu rapora ek olarak işlenir (M5/M6 deseni).
- Ön kayıt: [`m8-protocol.md`](m8-protocol.md) v1 (Kapı A) ve v1.1 (hash'ler ve §11 uygulama sapmaları, U 2026-10-30); [`m8-alloc-pilot-protocol.md`](m8-alloc-pilot-protocol.md) (U 2026-10-30). ADR-0023, D34.
- Claude Code bu rapora yalnızca operasyonel alanları yazar (m8-protocol §7). Getiri farkı, güven sınırı, σ̂_d ve drawdown değerleri burada yoktur; `trading.allocator_*` tabloları ve `/allocator` uç noktası gerçek ortamda Claude Code tarafından okunmadı.

## 1. Kullanıcı kararları (U)
| # | Karar |
|---|---|
| U-M8-1…U-M8-5 (D34) | Gölge öneriler tüm paper stratejileri için; bant `m × w_T11`, m ∈ [0,5; 1,5] ya da veto; kapanış kabul edilen sapma; `gemini-3.5-flash-lite`, pilot 1,00 / yeterlilik 3,00 USD (Kapı A, 2026-10-29) |
| A-1…A-7 | Non-inferiority marjı yılda −%3, drawdown toleransı 0, geçerli öneri oranı ≥ 0,80, süre kuralı 28–56 gün, ≥ 10 geçerli öneri, 30 sn zaman aşımı, 14 günlük pilot (Kapı A, 2026-10-29) |
| v1.1 | §8 hash'leri ve §11 sapmaları: yeterlilik kontrolü `SECURITY DEFINER` fonksiyonlarıyla, rapor araçları `ops_owner` ile, `Active → Shadow` kayıtlı protokol ister (2026-10-30) |
| Kapanış | M8, C2 tamamlandıktan sonra kapanır. C3 (pilot raporu ve yeterlilik protokolü) ve sonrası arka planda sürer (2026-10-31) |

## 2. Kapı C adımları
| # | Adım | Durum |
|---|---|---|
| C0 | Migration 0011, Gemini runtime anahtarı, Trading.Host yeniden derleme | **Tamamlandı (kullanıcı).** İlk denemede imaj derlemesi başarısız oldu: Dockerfile yeni projeleri kopyalamıyordu. Düzeltme `e5bf632`; Research.Host'taki eksik `SourceGateway` kopyası da düzeltildi; yeni mimari test. Migration `already applied 11`. İlk döngü `outcome=ok`, `killSwitch=False`; lifecycle `PaperTrading=5`; Testnet `enabled=True`, açık run 0, bilinmeyen emir 0, korumasız pozisyon 0. Beş stratejide son 7 günde kritik hata 0. Bellek kontrolü (< 384 MiB) bu çıktıda raporlanmadı |
| C1 | Sentetik spike (`alloc-spike`) | **Tamamlandı:** üç senaryo `PASS`, hepsi `outcome proposal`. `candidates-only`: 3 öğe, 2 override; `with-holdings`: 3 öğe, 3 override; `injection-in-name`: 3 öğe, 1 override. Üçünde de clamp 0, veto 0, güvenlik olayı 0. İş `alloc-ae4656b06498`: 0,005031 USD (bütçe 0,05) |
| C2 | Pilot protokolünün kaydı ve `Disabled → Shadow` | **Tamamlandı:** pilot, 14 gün, 1,00 USD, `gemini-3.5-flash-lite`; protokol hash'i `709c26e99f63eb233a2f2719035fadc760a712e0046c788fe61704fbaf5fea51`. Mod geçişi 2026-10-30'da Ops API ve DB trigger'ı tarafından kabul edildi; gölge mod bir sonraki döngüden itibaren geçerli |
| C3 | Pilot raporu, `N`, yeterlilik protokolünün commit'i ve kaydı | **Arka planda** (en erken ~2026-11-13): `m8-allocator.ps1 alloc-report --protocol docs/m8-alloc-pilot-protocol.md` |
| C4 | Kabul raporu | Bu belge |
| sonra | Pencere bitince `alloc-qualify` → `TaskQualification`; `Passed` ise `qualifiedModels` ve `Shadow → Active` ayrı kullanıcı kararları | Arka planda (en erken ~2026-12 ortası) |

## 3. Kabul kriterleri (roadmap M8; m8-protocol §10)
| Kriter | Kanıt | Sonuç |
|---|---|---|
| Limit dışı öneriler clamp ediliyor ya da reddediliyor | Kapı B: §5.3 tablosu testleri, rastgele override'larla I-3 ve I-4 property testleri (asset %20, toplam %80, deneysel %10, küme %60 hiç aşılmıyor; çarpan bantta). C1'de gerçek model bant içinde kaldı (clamp 0) | ✅ |
| Çıkışları engelleme girişimi etkisiz | Kapı B: I-2, I-5; `exit-interference` güvenlik olayı testleri; kalibrasyon kolu "Attacker" (%100 yakalandı). Kernel çıkış, stop ve Testnet stop senkronu allocator yolundan önce ve ondan bağımsız çalışıyor. C1 `with-holdings` ve `injection-in-name` senaryolarında güvenlik olayı 0 | ✅ |
| LLM durdurulduğunda ya da hata verdiğinde varsayılan politika devreye giriyor | Kapı B fault injection: kill switch, zaman aşımı, sağlayıcı hatası, şema, kanıt, bütçe, anahtar yok, nitelikli model yok, catch-up mumu. Her durumda T11; döngü durmuyor | ✅ |
| Gölge mod ≥ 4 hafta tamamlandı | Gölge mod canlı başlatıldı (C2). Pilot (14 gün) ve yeterlilik penceresi (28–56 gün) arka planda sürüyor | ⏳ **Kabul edilen sapma** (U-M8-4) |
| AllocationProposal protokolü ve sonucu kaydedildi | Pilot protokolü kayıtlı (`709c26e9…`). Yeterlilik protokolü pilot sonrası (C3) yazılır; `TaskQualification` pencere sonunda | ⏳ **Kısmen;** kabul edilen sapma |
| Aktif moda geçiş yalnızca nitelik varsa ve kullanıcı onayıyla | Kapı B: uygulama ve DB trigger'ı testleri (`confirm`, kayıtlı protokol, `Passed` yeterlilik, ≥ 28 günlük pencere, `ops_api` rolü). C2'de `Disabled → Shadow` aynı yoldan geçti | ✅ |
| Allocator ağırlığı yalnızca `Validated` stratejilere | Kapı B: `not-validated` testleri; aktif replay ve `ParityReplay` birebir. Bugün `Validated` strateji yok; aktif mod gerçekte hiçbir emri etkilemez | ✅ (çevrimdışı) |

## 4. Regresyon kilitleri
- M2'nin dört sonuç hash'i (`parity-m3`) ve 36 engine regresyon hash'i birebir.
- Kohort 0 ve Kohort 1 L1/L2 parity hash'leri ve Kohort 1 rapor özeti M5/M5b ile aynı (kuru `parity-m5`, Kapı B).
- Gölge modda gerçek adımlar, ledger ve hash'ler T11 ile birebir; gölge motor yeniden başlatmada model çağrısı olmadan kuruluyor (Kapı B testleri).

## 5. Kabul edilen sapmalar ve sınırlar
- **Gölge süre ve yeterlilik sonucu kapanıştan sonra** (U-M8-4, 2026-10-31): C3 ve yeterlilik penceresi tamamlanmadan kapatıldı. Sonuç `Passed` çıkmazsa (`Failed` ya da `Inconclusive`) T11 kalıcıdır (D20); aktif mod açılmaz.
- **C0'da derleme hatası:** eksik Dockerfile kopyaları. Kalıcı koruma: her Dockerfile'ın geçişli proje referanslarını kopyaladığını doğrulayan test. Ayrıca `m5-paper-start.ps1` ve `m6-update.ps1` Trading.Host'u artık `--force-recreate` ile başlatıyor: yeni imaj ve değişen secret dosyaları her başlatmada yeniden yükleniyor (test ile sabitlendi).
- **v1.1 uygulama sapmaları** (§1): `m8-protocol.md` §11 ve ADR-0023'te.
- **Dashboard'un "Allocator" sekmesi** yalnızca sentetik test verisiyle doğrulandı. Gerçek ortamdaki görünümü kullanıcı kontrol eder.
- **İlk pilot çağrısı henüz ledger'da yok** (2026-10-31 mutabakatı): pilot işi `alloc-709c26e99f63` ledger'da görünmüyor. Gölge çağrı yalnızca canlı bir 4h mumda giriş adayı varsa yapılır. Pilotun ilk günlerinde dashboard'daki karar sayaçlarının (`shadow/proposal`, fallback nedenleri) kullanıcı tarafından bir kez kontrol edilmesi önerilir. Yalnızca `no-key` ya da `provider-error` sayılıyorsa anahtar ya da bağlantı sorunu vardır.
- **Kohort 0'ın işlem sıklığı düşük:** yeterlilik sonucunun `Inconclusive` çıkma olasılığı yüksek (m8-protocol §6.3).

## 6. Bütçe
- **M8 AI harcaması (2026-10-31 itibarıyla):** 0,005031 USD (C1 spike). Pilot bütçesi 1,00 USD ve yeterlilik bütçesi 3,00 USD henüz kullanılmadı.
- **2026-10 toplamı:** 0,749711 USD ([`m4-ai-reconciliation-2026-10.md`](m4-ai-reconciliation-2026-10.md)); aylık 10 USD tavanının altında (D4).

## 7. Kabul
- **Kullanıcı kabulü (U, 2026-10-31):** M8, allocator kodu, testler ve canlı gölge modla kapatıldı. Pilot, yeterlilik protokolü ve yeterlilik penceresi arka planda sürer (kabul edilen sapma).
- **Açık kalanlar (kullanıcı):**
  - pilot raporu ve süre `N` (C3, en erken ~2026-11-13); yeterlilik protokolünün commit'i ve kaydı; `Shadow → Shadow` geçişi;
  - pencere sonunda `alloc-qualify`; `Passed` ise `qualifiedModels` ve `Shadow → Active` ayrı kararlar;
  - 2026-10 Google faturasının mutabakatı (ay kapanınca).
