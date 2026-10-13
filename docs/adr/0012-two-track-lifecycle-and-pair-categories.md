# ADR-0012: İki izli yaşam döngüsü ve parite kategorileri

- Durum: Kabul edildi (kullanıcı kararları U1, U5, U6, U7) · Tarih: 2026-10-13
- Uygulama durumu: **Planlandı, uygulanmadı**
- İlgili: ADR-0006, ADR-0011, `architecture.md` §6.4, `risk-model.md` §2

## Bağlam
- Testnet'te güvenli deneme, 6–12 aylık ileriye dönük doğrulamayı beklememeli.
- Paper ve Testnet sonuçları da ileriye dönük doğrulamanın yerini tutmamalı.
- Dört yıllık geçmiş şartı yalnızca çekirdek araştırma evreni içindir; daha yeni pariteler için ayrı bir politika gerekiyor.

## Karar

### 1. İki bağımsız eksen
**Operasyonel iz:**
`Candidate → Frozen → PaperTrading → TestnetActive → Disabled | Retired | Rejected`

- `Frozen → PaperTrading` otomatiktir (D3).
- `PaperTrading → TestnetActive` **kullanıcı onayı** gerektirir (D3).
- Testnet **doğrulama gerektirmez.**

**Doğrulama durumu:**
`Unvalidated → ProspectiveSealed → Validated | Failed | Inconclusive | Invalid`

- Bu durum bir view'dır ve yalnızca `eval_prospective` tarafından yazılan sonuçlardan türetilir.
- Registry, Ops API ve Research bu durumu set edemez. Paper ve Testnet metrikleri hesaba girmez.
- `Validated` gerektiren işler: "doğrulanmış" etiketi, LLM Allocator'ın ağırlık vermesi (M8), kapsam dışı her türlü yüksek riskli terfi.

**Operasyonel güvenlik eylemleri her zaman serbesttir** ve audit'e yazılır: devre dışı bırakma, kill switch'ler, yeni emirlerin engellenmesi, pozisyonların kapatılması. Bu eylemler kohort değerlendirmesini etkilemez; evaluator, dondurulmuş tanımı arşiv verisi üzerinde bağımsız olarak simüle eder.

### 2. Parite kategorileri
**Tanımlar:**
- `first_open` = sembolün arşivdeki ilk 1h mumunun açılış zamanı.
- `U0 = first_open + 30 gün` (listeleme sonrası karantina).
- `C = 2026-01-01T00:00:00Z`.

| Kategori | Kural | Araştırma penceresi |
|---|---|---|
| Çekirdek | `U0 ≤ 2022-01-01T00:00Z` (yani `first_open ≤ 2021-12-02`) ve `[2022-01-01, C)` aralığı kalite şartlarını sağlıyor | `[2022-01-01, C)` |
| E1 | `U0 ≤ 2025-01-01T00:00Z`, çekirdek değil, `[U0, C)` aralığında en az 365 gün kullanılabilir veri | `[U0, C)` |
| E2 | Geri kalan her şey (kesim sonrası listelenenler dahil) | Yok (araştırılamaz). Yalnızca **transfer:** dondurulmuş ve değiştirilmemiş bir strateji, `U0`'dan sonra yeni bir kohortla bu paritelere uygulanabilir |

**Veri kalitesi** (gevşetilmez):
- checksum doğrulanmış;
- OHLC kuralları sağlanmış;
- beklenen 1h mumların ≥ %99'u mevcut;
- tek bir boşluk ≤ 72 saat (kayıtlı ve piyasa genelindeki kesintiler hariç).

Kalite şartlarını karşılamayan parite bir alt kategoriye kaydırılmaz; elenir ve raporlanır.

**Doğrulama kısıtları:**
- `Validated` durumu **(strateji sürümü, parite kümesi)** çiftine bağlıdır ve kategoriler arasında taşınmaz.
- Raporlarda kanıt düzeyi etiketi bulunur: `core-4y`, `E1-<n>y`, `E2-forward-only`.
- Yetersiz geçmiş, hiçbir yerde 4 yıllık kanıta eşdeğer sayılmaz.

**Maruziyet:** E1 ve E2 paritelerinin toplam maruziyeti `ExperimentalPairsMaxExposure` (varsayılan %10) tavanına tabidir (`risk-model.md` §2).

## Gerekçe
- Deneme güvenliği (Testnet) ile istatistiksel kanıt (doğrulama) farklı sorulara yanıt verir. İkisini ayırmak, gereksiz beklemeyi kaldırırken holdout'un bağımsızlığını korur.
- Kategori sınırlarının açık tarihlerle tanımlanması ve kanıt düzeyi etiketleri, "kısa geçmiş"in sessizce "yeterli kanıt" muamelesi görmesini engeller.

## Sonuçlar
- Doğrulanmamış stratejiler Testnet'te "deneysel" etiketiyle çalışabilir.
- Deneysel paritelerin portföy etkisi, ayrı ve kullanıcı kontrollü bir tavanla sınırlanır.
