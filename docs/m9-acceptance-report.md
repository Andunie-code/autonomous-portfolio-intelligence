# M9 Kabul Raporu — Dashboard tamamlaması

- Durum: **M9 tamamlandı (kullanıcı kabulü, U, 2026-11-01).**
- Ön kayıt: [`m9-m10-protocol.md`](m9-m10-protocol.md) v1 (Kapı A `d202bae`) ve ADR-0024; M9a kısmı M5b'de (ADR-0020, D32). Kapı B: `ae630ca` (`verify.ps1` yeşil, 794 test).
- Claude Code dashboard'u ve veri uç noktalarını gerçek ortamda açmadı ve çağırmadı (ADR-0020 §6). Gerçek ortamdaki görsel kontrol kullanıcınındır. Bu rapor yalnızca operasyonel alanlar içerir.

## 1. Kapı adımları
| # | Adım | Durum |
|---|---|---|
| A | Protokol, ADR-0024, D35 | Tamamlandı (`d202bae`, U 2026-11-01) |
| B | Çevrimdışı kod ve testler | Tamamlandı (`ae630ca`; 794 test) |
| C | Güncelleme (`m6-update.ps1`) ve dashboard'un tarayıcı kontrolü | Tamamlandı (kullanıcı, 2026-11-01) |

## 2. Kabul kriterleri (roadmap M9)
| Kriter | Kanıt | Sonuç |
|---|---|---|
| Emirden veri setine kadar zincir gezilebiliyor | Paper dolumu: `DashboardTests` (dolum → L2 → L1 → sleeve → `definitionHash`/`structureHash` doğrulanmış → `manifestHash` → deney → `DatasetSnapshot`). Testnet borsa emri: `TestnetTradingTests.An_exchange_order_opens_the_chain_to_the_dataset_and_a_testnet_fill_shows_its_order` (`clientOrderId` ve borsa emir no ile; durum geçmişi, trade'ler, dolumlar; Testnet dolumundan emre dönüş). Allocator düğümü (çarpan, öneri, DecisionTrace): `DashboardTests.Traces_costs_the_budget_and_the_allocator_node_are_shown_without_model_text` | ✅ (sentetik veri). Gerçek bir Testnet emrinden zincir, Kohort 0 ilk emrini verdiğinde kullanıcı tarafından görülecek (§4) |
| DecisionTrace, degraded işaretleri ve LLM acil durdurma durumu görünüyor | `/ai/traces`: istenen → gerçek model, `degraded` rozeti, validator adları ve ret sayısı, güvenlik ihlali sayısı, gecikme, token, tahmini ve gerçek maliyet; validator metni dönmüyor (test). `/system`: trading ve AI kill switch'leri aktör, tetik, neden ve zamanla; alarm şeridi ve tablosu (A1, A2) | ✅ |
| Görev sınıfı bazında maliyet görünüyor | `/ai/costs`: ay × görev sınıfı × iş türü, ay ara toplamı, görev sınıfı toplamı, bu ayın işleri ve iş tavanları, 10 USD tavana göre kullanım ve bütçe çubuğu (%80 uyarı). Testte 8,5/10 USD → %85 ve A3 uyarısı | ✅ |

## 3. Güvenlik ve regresyon
- Yeni rotalar (`/provenance/order/{id}`, `/alarms`) yalnızca GET'tir; H24 kuralları (401, 421, yasaklı alt dizgi yok, yanıtta secret yok) testlerle sabittir. Yeni POST yoktur; `research_app` izinleri ve `ops_api`'nin salt-okunur yetkileri değişmedi; migration yoktur.
- Execution AI'sız kalır: bütçe tavanı Trading.Host'tan değer olarak gelir (ADR-0023 §1).
- M2 hash'leri, M3 golden'ları ve Kohort 0/1 parity kilitleri `verify.ps1` içinde değişmeden geçti.

## 4. Sınırlar ve açık kalanlar
- Kriterlerin kanıtı sentetik veriyle yapılan HTTP ve sorgu testleridir (ADR-0020 §6). Gerçek ortamdaki görünüm kullanıcı tarafından kontrol edildi (Kapı C).
- Kapanışta Kohort 0'ın Testnet'te emri yoktu (`positions=0`). Gerçek bir Testnet emrinden zincirin tarayıcıda açılması, ilk emirden sonra kullanıcı tarafından kontrol edilir ve M10 prova ekine not edilir.
- Allocator gölge modda olduğundan gerçek adımlarda `AllocatorAdjusted` olayı oluşmaz; zincir gölge öneriyi ve izini gösterir. Aktif mod ayrı karardır (M8).
