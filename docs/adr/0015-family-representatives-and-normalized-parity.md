# ADR-0015: Aile temsilcisi tabanlı benzerlik kontrolü ve normalize parity

- Durum: Kabul edildi · Tarih: 2026-10-14
- Karar türü: Teknik kararlar T21 ve T22
- Uygulama durumu: **Aile temsilcisi M3'te; L1/L2 parity ve L3 raporu M5'te uygulandı (2026-10-20).** Kohort 0 parity replay birebir (`m5-parity-results.md`).
- İlgili: ADR-0006, ADR-0012, ADR-0013, ADR-0014, `architecture.md` §6.3–§6.5

## 1. Aile benzerlik kontrolü (T21)

### Bağlam
`architecture.md` §6.3, kural grafiği farklı olsa da araştırma verisinde pozisyon korelasyonu ≥ 0,9 ya da giriş Jaccard örtüşmesi ≥ 0,8 olan bir adayı mevcut aileye atar. Her deneyin tam pozisyon serisini saklayıp yeni adayı tüm geçmiş deneylerle karşılaştırmak, deney sayısı yüzlere ve binlere ulaştığında 16 GB RAM'de bellek ve işlem darboğazı yaratır.

### Karar
- **Aile temsilcisi:** Her ailenin, kayıt sırasına göre **ilk** üyesi. Deterministiktir ve değişmez.
- **Temsilci için saklananlar** (yalnızca temsilci için; diğer deneylerin serileri bu amaçla saklanmaz):
  - parite başına, araştırma dönemindeki 1h ızgarada **giriş olayı bitseti** (`[2022-01-01, C)` için parite başına yaklaşık 4,4 KB);
  - **indirgenmiş pozisyon vektörü**: 1d ızgarada parite başına maruziyet, `int8` olarak nicemlenmiş.
- **Karşılaştırma:** Yeni aday yalnızca aile temsilcileriyle karşılaştırılır (aile sayısı kadar karşılaştırma). Artefaktlar veritabanından parti halinde, akışlı okunur; hepsi aynı anda belleğe alınmaz.
  - Jaccard: (parite, mum) giriş olayları üzerinden.
  - Korelasyon: ortak 1d ızgarada; ortak olmayan pariteler sıfır maruziyet sayılır.
- Eşikler (0,9 ve 0,8) değişmedi; TBD olarak M3'te gözden geçirilir (`requirements.md` §8).
- Veri snapshot'ı değişirse (yeni `DatasetSnapshot`, `C`'nin ilerletilmesi) temsilci artefaktları aynı strateji tanımlarından yeniden üretilir.

### Sonuçlar
- Bellek ve disk ihtiyacı deney sayısıyla değil aile sayısıyla büyür.
- **Kabul edilmiş risk:** Temsilciden adım adım uzaklaşan bir varyant zinciri (her biri bir öncekine benzer, ama temsilciye benzemeyen) ayrı bir aile gibi görünebilir. Yapı hash'i, global sınırlar ve "n denemeden k Validated" raporlaması asıl güvence olmaya devam eder (ADR-0006).

## 2. Normalize parity ve fark raporu (T22)

### Bağlam
Paper sermayesi 10.000 USDT nominaldir (`requirements.md` §4). Canlı paper'da birden fazla strateji ve kohort ortak portföy sermayesini paylaşır ve sermaye PnL ile değişir. Evaluator ise stratejiyi arşiv verisi üzerinde izole olarak, sabit başlangıç sermayesiyle simüle eder. Bu yüzden mutlak emir büyüklükleri birebir eşleşmez.

### Karar
Parity üç katmanda tanımlanır:

| Katman | Ne karşılaştırılır | Beklenen |
|---|---|---|
| **L1 — sinyal paritesi** | Aynı mumlarla: sinyaller, giriş ve çıkış zamanları, stop seviyeleri ve tetiklenmeleri (ADR-0014), risk öncesi birim hedef ağırlıklar | **Birebir** |
| **L2 — karar paritesi** | Aynı portföy durumu ve `RiskConfig` ile: risk motoru ve sizer kararları (onay, kırpma, ret) | **Birebir** |
| **L3 — sonuç karşılaştırması** | İşlem zamanlaması eşleşme oranı, işlem başına yüzde getiri, maruziyete göre normalize getiri, slippage (bps) | **Yalnızca rapor.** Mutlak emir büyüklüğü ve dolar tutarları karşılaştırılmaz |

- **`Frozen → PaperTrading` parity replay** L1 ve L2'den oluşur.
- **Fark raporu** (`architecture.md` §6.5) L3'tür. Yalnızca bilgilendiricidir ve doğrulamaya girmez.
- L3 farkları şu kategorilere ayrılır:
  - ortak sermaye ve portföy kısıtları (risk kırpması, maksimum pozisyon sayısı, varlık ve toplam maruziyet tavanları);
  - `MIN_NOTIONAL` nedeniyle atlanan emirler;
  - dolum ve slippage farkları (özellikle Testnet);
  - canlı mumlarla arşiv mumları arasındaki farklar.
- Ortak portföyde strateji bazında normalize getiri hesaplanabilmesi için `OrderIntent` ve ilgili ledger kayıtları strateji ve kohort kimliğiyle etiketlenir.

### Sonuçlar
- Parity iddiası, mutlak tutarların değil karar mantığının aynı olduğu yerde yapılır.
- Bir stratejinin işleminin paper'da portföy kısıtı nedeniyle hiç açılmaması, parity hatası değil L3'te raporlanan bir farktır.
