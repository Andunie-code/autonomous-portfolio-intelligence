# M7 Kabul Raporu — Kontrollü web araştırması

- Durum: **M7 tamamlandı (kullanıcı kabulü, U, 2026-10-28).**
  - Kapı A (`2ff940b`), Kapı B (`e82e4dd`, 711 test), C1 düzeltmesi (`8235e96`, protokol v1.2), C1 ve C3 kaydı (`37dd502`) ve C4 tamamlandı.
  - **Sonuç:** FindingExtraction yeterliliği **`Failed`** (`TaskQualification` 2). Web araştırması kapalı kalır (D20).
- Ön kayıt: [`m7-protocol.md`](m7-protocol.md) v1, v1.1 (pilot hash'leri ve sapmalar, U) ve v1.2 (API sorgusu düzeltmesi). ADR-0022.
- Claude Code bu rapora yalnızca operasyonel alanları yazar (m7-protocol §11). Makale başlıkları, metinler ve bulgu içerikleri burada yoktur.

## 1. Kullanıcı kararları (U)
| # | Karar |
|---|---|
| U-M7-1…U-M7-6 | Kapsam ve kapılar; yalnızca kesim öncesi arXiv sürümleri; Semantic Scholar ertelendi; model `gemini-3.5-flash-lite`; bütçe pilot 0,50 / yeterlilik 3,00 USD; arXiv API'si `robots.txt` yerine API koşullarına tabi (Kapı A, 2026-10-28) |
| v1.1 | NFKC yerine açık uyumluluk tablosu, idempotent paragraf ayracı, §7'deki betimleyici notun düzeltilmesi (2026-10-28) |
| C4 sonrası | v2 döngüsü açılmaz. `qualifiedModels: []` kalır; web araştırması kapalıdır (2026-10-28) |

## 2. Kapı C adımları
| # | Adım | Durum |
|---|---|---|
| C0 | Migration 0010 (`m5-migrate.ps1`) | Tamamlandı (kullanıcı) |
| C1 | arXiv araması, ≤ 10 belge, provenance raporu | **Tamamlandı.** İlk aramada API HTTP 500 döndü; sorgu düzeltildi (v1.2 §15) ve düzeltme gerçek API'de çalıştı. 4 belge (3 PDF, 1 özet); provenance sorunu 0 |
| C2 | Model sürümü spike'ı | **Koşulmadı** (ledger'da 2026-10-28 tarihli spike işi yok). Telafi: router yeterlilik modunda `modelVersion`'ı protokoldeki modelle karşılaştırır ve eşleşmeyen yanıtı degraded sayar. Pilot ve yeterlilikte degraded nedeniyle başarısız olan öğe yok; ret nedenleri yalnızca `unsupported-number` |
| C3 | FindingExtraction pilotu | **Tamamlandı:** 19/20, ihlal 0, 0,028655 USD ([`m7-fe-pilot-results.md`](m7-fe-pilot-results.md)) |
| C4 | Yeterlilik (`n` = 30) | **`Failed`:** 27/30, p = 0,1227, ihlal 0, 0,044213 USD ([`m7-fe-qualification-results.md`](m7-fe-qualification-results.md)) |
| C5 | `qualifiedModels` ve production extraction | **Uygulanmaz** (nitelik yok; U) |
| C6 | Bulgu → hipotez ve web'li/web'siz karşılaştırma | **Kapalılık raporu** ([`m7-web-comparison.md`](m7-web-comparison.md)); gerçek denemesi olan bir koşu yapılmadı, global sayaç değişmedi |
| C7 | Mutabakat ve kabul | 2026-10 ledger'ı güncellendi ([`m4-ai-reconciliation-2026-10.md`](m4-ai-reconciliation-2026-10.md)): toplam 0,744680 USD, bunun 0,072868 USD'si FindingExtraction. Fatura ay kapanınca girilir |

## 3. C1 — kaynaklar ve provenance
- **Arama:** 4 kullanılabilir kayıt; kesim sonrası, kategori ya da ayrıştırma nedeniyle düşürülen 0.
- **robots.txt:** `arxiv.org` → 200, PDF'e izin verdi.
- **Belgeler:**
  - 1 `2508.02403v1` (Pdf; normalizer 5 karakter sildi, `suspicious` işaretli);
  - 2 `2407.12150v1` (Pdf);
  - 3 `2108.11921v1` (Pdf);
  - 4 `1802.03708v8` (Abstract; `/pdf/` 404 döndü, kural gereği özete düşüldü).
- **Kesim kuralı:** dört belgenin v1 ve kullanılan sürüm tarihleri `< 2026-01-01`.
- **Provenance raporu** (`provenance-20261028T185011Z.json`): sorunlu 0, önbellek bozulması 0.
- **Politika ihlali:** C1'de gözlenmedi.

## 4. Kabul kriterleri (roadmap M7; m7-protocol §13)
| Kriter | Kanıt | Sonuç |
|---|---|---|
| Allowlist dışı ve redirect'le ulaşılan URL'lere erişilemiyor | Kapı B: `AccessTests` (host, yol, sorgu, şema, port, userinfo, fragment, traversal; 301/302/303/304/307/308'in hepsi reddedilir, aynı host'a olanlar dahil; 403 sonrası host kilidi). C1'de ihlal yok | ✅ |
| Injection setinde güvenlik değişmezleri ihlal edilmiyor (0) | Kapı B: I1–I5 senaryoları ve saldırgan kol. C3 ve C4'te gerçek modelde güvenlik ihlali **0** (50 öğe, 15 injection öğesi) | ✅ |
| Uydurma alıntılar reddediliyor | Kapı B: `QuoteVerifier` testleri ve uydurucu kol (50/50 ret). C3 ve C4'te uydurma alıntı 0; kabul edilmiş doğrulanmamış alıntı 0 | ✅ |
| Provenance kayıtları eksiksiz | C1 provenance raporu: 4/4 belgede URL, kanonik URL, alınma zamanı, içerik ve metin hash'leri, sürüm tarihleri, çıkarıcı ve normalizer sürümleri; önbellekten yeniden hash eşleşmesi | ✅ |
| Bulgudan türeyen hipotez normal hattan geçiyor | Kapı B: `M7FindingHypothesisTests` (sentetik, doğrulanmış bulgu → DSL, deneme sayacı, G1–G6; doğrulanmamış ya da bilinmeyen bulgu, deneme açılmadan reddedilir) ve DB trigger'ı. Production'da bulgu yok (web kapalı) | ✅ (çevrimdışı) |
| FindingExtraction yeterliliği ön kayıtla yapıldı ve kaydedildi; nitelik yoksa web araştırması kapalı | `TaskQualification` 2 (`Failed`), protokol `9e8bc4ad…` commit'li belgeyle aynı. Kapalılık: router `Skip`, tool'lar kapalılık notu, `extract` 3 koduyla çıkar (testler) | ✅ |
| Web'li ve web'siz karşılaştırma raporu hazır | `web-compare` aracı ve testleri; kapalılık raporu (m7-protocol §9) | ✅ (kapalılık biçiminde) |

## 5. Kabul edilen sapmalar ve sınırlar
- **C2 koşulmadı** (§2): model sürümü eşleşmesi router'ın degraded kontrolüyle dolaylı olarak görüldü.
- **v1.1 uygulama sapmaları** (NFKC tablosu, paragraf ayracı) ve **v1.2 sorgu düzeltmesi:** CHANGELOG'da ve ADR-0022'de.
- **Gerçek belge sayısı küçük:** 4 belge, yalnızca provenance kanıtı için. Production'da hiçbir bulgu çıkarılmadı.
- **Web'li karşılaştırma yapılamadı:** nitelik yok. Bu, M7'nin bilinçli bir olası sonucudur (m7-protocol §10, §13).

## 6. Bütçe
- **M7 AI harcaması:** 0,072868 USD (pilot 0,028655 + yeterlilik 0,044213). Ön kayıtlı bütçeler 0,50 ve 3,00 USD'nin altında.
- **2026-10 toplamı:** 0,744680 USD; aylık 10 USD tavanının altında (D4).

## 7. Kabul
- **Kullanıcı kabulü (U, 2026-10-28):** M7, FindingExtraction yeterliliğinin `Failed` sonucu ve web araştırmasının kapalı kalmasıyla kapatıldı.
- **Açık kalanlar:**
  - 2026-10 Google faturasının mutabakatı (ay kapanınca, kullanıcı);
  - FindingExtraction için olası bir v2 döngüsü (yeni prompt, protokol ve set; ayrı kullanıcı kararı).
