# ADR-0022: SourceGateway, tool'suz Extractor, alıntı doğrulaması ve tek kollu yeterlilik

- Durum: **Kabul edildi (Kapı A, kullanıcı onayı 2026-10-28)** · Tarih: 2026-10-28
- Karar türü:
  - **U:** `m7-protocol.md` U-M7-1…U-M7-6 (zaman filtresi, Semantic Scholar'ın ertelenmesi, model, bütçe, `robots.txt` ele alışı).
  - **T:** modül yapısı, guard ve önbellek, kütüphaneler, normalizer, kabul hattı, harness'ın tek kollu modu, bulgu → hipotez yolu.
- Uygulama durumu: henüz uygulanmadı (Kapı B).
- İlgili: ADR-0006, **ADR-0008** (bu ADR onun uygulamasıdır), ADR-0010, ADR-0013, ADR-0016, ADR-0017; `m7-protocol.md`; `architecture.md` §4, §8; `sources.md` S-X1…S-X12.

## Bağlam
ADR-0008 kontrollü web araştırmasının ilkelerini koydu: kendi getiricimiz, allowlist, provenance, tool'suz Extractor, alıntı doğrulaması, nitelik yoksa kapalılık. Kodda bu bileşenlerin hiçbiri yok. `TaskClass.FindingExtraction` enum'da, politikası `ai.v1.json`'da (`qualifiedModels: []`, `onUnavailable: Skip`) bulunuyor.

Kapı A doğrulamasında iki yeni durum çıktı:
- **`export.arxiv.org/robots.txt` tüm botlara `Disallow: /` diyor** (S-X5). Oysa arXiv'in API kullanım koşulları programatik erişim için bu host'u gösteriyor (S-X1, S-X7, S-X8).
- **`arxiv.org/robots.txt` okunamadı** (HTTP 503, S-X6).

Ayrıca kesimden sonra yayımlanan bir makale, holdout dönemine ait piyasa davranışını anlatabilir. Bu bir holdout sızıntısı yoludur (ADR-0006).

## Karar

### 1. Projeler ve bağımlılıklar
| Proje | Bağımlılıklar | İçerik |
|---|---|---|
| `PortfolioIntelligence.SourceGateway` (`src/Modules/SourceGateway`, yeni) | Kernel; `PdfPig`, `AngleSharp`, `Npgsql` | `SourceRequestPolicy`, `SourceGuardHandler`, `DomainRateLimiter`, `RobotsPolicy`, `SourceCache`, `ArxivClient`, `PdfTextExtractor`, `HtmlTextExtractor`, `TextNormalizer`, `SourceDocument`, `Finding`, store'lar |
| `PortfolioIntelligence.Research` (mevcut) | + SourceGateway | `Sources/`: `FindingExtractor`, `QuoteVerifier`, `FindingNumericValidator`, `FindingSanitizer`, `WebResearchGate`, Researcher backend'inin bulgu uygulaması; `Qualification/FindingExtractionQualification.cs` |
| `PortfolioIntelligence.Researcher` (mevcut) | değişmez (Kernel, Ai) | `SearchFindings`, `RequestSourceResearch` tool sözleşmeleri ve port genişlemesi |
| `PortfolioIntelligence.SourcesCli` (`src/Tools/SourcesCli`, yeni) | SourceGateway, Research, Infrastructure | `search`, `fetch`, `extract`, `docs`, `provenance-report` |

**Mimari testlerle korunan kurallar:**
- SourceGateway yalnızca Kernel'e bağlıdır; Execution'a, Trading.Host'a, HoldoutEvaluator'a ve Ai'ya bağlanmaz.
- `PdfPig` ve `AngleSharp` yalnızca SourceGateway'de bulunur.
- Researcher SourceGateway'i referans almaz. Yasaklı kaynak taraması (`System.Net.Http`, `HttpClient`, `Npgsql` …) aynen geçerlidir.
- SourceGateway kaynaklarında `new HttpClient()` yalnızca guard handler'la kurulan istemcide bulunur. Testlerde ağ yoktur (`NoNetworkTests` taraması yeni test projesini kapsar).

### 2. Erişim
- **Allowlist:** gömülü `Config/sources.v1.json` (`m7-protocol.md` §2.1). HTTPS, varsayılan port, yalnızca GET; yol ve sorgu allowlist'i; sürümlü arXiv kimliği zorunlu.
- **Redirect:** her 3xx reddedilir. `ArchiveGuardHandler` ve `TestnetGuardHandler` ile aynı kural; `SocketsHttpHandler { AllowAutoRedirect = false }`.
- **Boyut, içerik türü ve süre sınırları;** 403'ten sonra o host'a istek yok; 429/503'te `Retry-After`.
- **Rate limit:** host başına tek bağlantı ve ≥ 3 sn aralık (S-X1'deki sıkı kural; S-X8'deki daha gevşek kural kullanılmaz).
- **`robots.txt` (U-M7-6):**
  - `export.arxiv.org/api/query` belgelenmiş bir API'dir ve API kullanım koşullarına tabidir; `robots.txt` muafiyeti bu satırla sınırlıdır.
  - Diğer bütün yollar için `robots.txt` bağlayıcıdır: 404/410 → kısıt yok; 2xx → kurallar; diğer her durum → host yasak.
  - Sonuç olarak `arxiv.org/robots.txt` okunamadığı sürece PDF indirilmez ve Extractor yalnızca API'den gelen özetle (CC0) çalışır.
- **Zaman filtresi (U-M7-2):** Atom'daki `published < C` ve `updated < C`; sunucu tarafı `submittedDate` süzgeci ek güvencedir, bağlayıcı kontrol istemcidedir. Kontrol üç yerde tekrarlanır: `ArxivClient`, `SourceDocument` kaydı (DB `CHECK`) ve `HypothesisStore`.
- **Önbellek:** repo ve OneDrive dışında (`ArchiveCacheDirectory` kuralı ortak yardımcıya taşınır, davranışı değişmez); URL ve içerik SHA-256'sı; TTL 30 gün.

### 3. Metin çıkarma
| Seçenek | Lisans | Native | Değerlendirme | Sonuç |
|---|---|---|---|---|
| **PdfPig** 0.1.16 | Apache-2.0 | yok | Harf düzeyinde konum, font boyutu, renk ve render modu: gizli metin süzülebilir. Kararlı sürüm yavaş çıkıyor (0.1.16, 2026-08-22) | **Seçildi** |
| PDFsharp | MIT | yok | Oluşturma odaklı; metin çıkarma ve düzen bilgisi zayıf | Hayır |
| iText 9 | AGPL / ticari | yok | Lisans kişisel ve kapalı kullanımla uyumsuz | Hayır |
| Docnet.Core, PDFium türevleri | BSD / Apache | **var** | Native binary şartını ihlal eder; Docker imajında ek paket | Hayır |
| **AngleSharp** 1.8.4 | MIT | yok | Standart uyumlu DOM; `hidden`, `aria-hidden` ve satır içi stil süzgeci kolay | **Seçildi** |
| HtmlAgilityPack | MIT | yok | Hafif, ama standart dışı ağaç; gizli öğe kuralları elle yazılır | Hayır |

- Sürüm kuralı ADR-0017 ile aynıdır: en az 14 gün önce yayımlanmış en yeni kararlı sürüm (S-X12). AngleSharp 1.8.5 ve 1.8.6 bu yüzden seçilmedi. Güncelleme ayrı bir commit'le yapılır.
- Normalizer (`text-normalizer v1`; NFKC, kontrol, sıfır genişlikli ve bidi karakterleri, tire/tırnak birliği, tireleme), hem Extractor girdisine hem alıntı doğrulamasına uygulanır. Böylece iki taraf aynı metni görür.

### 4. Extractor ve kabul hattı
- Extractor tool'suzdur ve tek bir router çağrısıdır (`FindingExtraction`). Girdi sınırlayıcılarla çevrilir; sistem talimatı belgenin talimat değil veri olduğunu söyler.
- **Kabul hattı (her bulgu için):** şema → `QuoteVerifier` (normalize metinde tam alt dizgi; bulanık eşleşme yok) → `FindingNumericValidator` → `FindingSanitizer` → belge filtresi. Reddedilen bulgu nedeniyle kaydedilir ve Researcher'a gitmez.
- **Etki sınırı:** bulgu yalnızca bir hipoteze kaynak olarak atanabilir. Kapı, eşik, protokol, risk ya da bütçe üzerinde etkisi yoktur. DSL bulgudan otomatik ayrıştırılmaz.

### 5. Tek kollu yeterlilik
- `QualificationProtocol`'e `Design` (`TwoArmFisher` | `OneArmBinomial`) ve `P0` alanları eklenir; ikisi protokol hash'ine girer.
- Tek kollu modda karar: güvenlik ihlali → `Failed`; eksik örneklem → `Inconclusive`; kesin binom testi H0: p ≤ p0 için `p < α` → `Passed`; aksi halde `Failed`.
- Mevcut iki kollu yol değişmez. HypothesisGeneration testleri ve kayıtları aynen kalır; v1/v2 protokol hash'leri değişmez (yeni alanlar yalnızca tek kollu protokolde hash'e girer).
- **Gerekçe:** bulgu çıkarmanın deterministik bir alternatifi yok; LLM'siz durumda web araştırması kapalıdır. İki kollu bir karşılaştırma, "hiç bulgu çıkarmamak" ile kıyas olurdu ve anlamsızdır. Soru güvenilirliktir: başarı oranının alt sınırı.

### 6. Researcher ve hipotez yolu
- Tool kümesi 6'dan 8'e çıkar: `SearchFindings` (yalnızca doğrulanmış bulgu; ham metin yok) ve `RequestSourceResearch` (yalnızca kayıt; ağ yok).
- Bulgudan hipotez insan tarafından yazılır (`ApprovedHypothesisSource`). `research.hypotheses.source_finding_ids` sütunu eklenir; `HypothesisStore` bulguların doğrulanmış ve kesim öncesi olduğunu kontrol eder. Origin değerleri ve `TrialOrigin` değişmez.
- **Kapalılık:** `WebResearchGate`, router'daki nitelik kuralını kullanır. Nitelik yoksa Extractor çağrı yapmaz, tool'lar kapalılık notu döndürür; fetch ve provenance hattı çalışır.

### 7. Kalıcılık (migration 0010)
- `sources` şeması:
  - `source_documents`: URL, kaynak türü, trust tier, arXiv kimliği ve sürümü, `published_at`, `version_at` (`CHECK (version_at < '2026-01-01T00:00:00Z')` arXiv için), alınma zamanı, içerik ve metin SHA-256, boyut, içerik türü, çıkarıcı sürümü, süzülen karakter sayıları, `suspicious`, `truncated`;
  - `findings`: belge, `DecisionTrace` kimliği, `kind`, `claim`, `quote`, ofsetler, `numbers`, `verified`, `rejection_reason`;
  - `research_requests`: konu, kategoriler, durum.
- `research.hypotheses.source_finding_ids bigint[]`.
- Tüm tablolar append-only (`audit.reject_change()` trigger deseni). `research_app` için `SELECT, INSERT` ve sequence `USAGE`.

### 8. Claude Code korkulukları (T17 eki)
- Deny kuralları: `export.arxiv.org` (WebFetch ve shell), `arxiv.org/pdf` (shell), `m7-*.ps1`.
- CLAUDE.md kuralı: makale içeriği, API yanıtı, önbellek dizini ve `sources.*` metin alanları Claude Code tarafından okunmaz; Claude Code yalnızca operasyonel alanları görür (`m7-protocol.md` §11).

## Değerlendirilen alternatifler
| Alternatif | Neden seçilmedi |
|---|---|
| `robots.txt`'ye her yerde harfiyen uymak | `export.arxiv.org` tamamen yasak olur ve arXiv'e erişim kalmaz; arXiv'in kendi API kılavuzuyla çelişir (S-X5). Kullanıcı kararı U-M7-6 |
| `robots.txt` okunamazsa izin vermek | Yaygın uygulama 5xx'i "tamamen yasak" sayar. arXiv, 403 sonrası ısrarı saldırı sayıyor (S-X7); muhafazakâr taraf seçildi |
| Yalnızca özetlerle çalışmak (PDF yolu hiç olmadan) | `robots.txt` izin verdiğinde daha zengin bulgu imkânını kapatır. Mevcut karar zaten okunamayan `robots.txt`'de bu davranışa düşer |
| Aynı host'a 3xx'e izin vermek | Sürümlü URL kullanıldığı için meşru redirect beklenmez; tek kural daha basit ve test edilebilir |
| Bulanık alıntı eşleşmesi (edit distance) | Uydurma alıntı ile hafif değiştirilmiş alıntı arasındaki sınırı belirsizleştirir. Tolerans yalnızca normalizer'dadır |
| Gerçek makalelerle yeterlilik seti | İnsan etiketlemesi ve sabitlenemeyen içerik; ayrıca Claude Code içeriği okumamalı (§8). Sentetik set kurgu gereği etiketlidir |
| Extractor'ın Gemini URL context'le çalışması | T13; ADR-0008 rev. 3 |
| LLM ile hipotez üretimi (bulgu → HypothesisGeneration) | HypothesisGeneration'ın niteliği yok (v1 `Failed`, v2 `Inconclusive`) |

## Sonuçlar
- Web araştırması, FindingExtraction `Passed` olmadan ve kullanıcı `qualifiedModels` girişini eklemeden açılmaz.
- `arxiv.org/robots.txt` okunamadığı sürece bulgular özetlerden gelir; bu, bulguların derinliğini sınırlar.
- Kabul edilmiş artık riskler:
  - injection içeriğinin doğrulanabilir bir alıntı olarak belgede bulunması (koddaki doğrulama bunu yakalayamaz; yeterlilikteki değişmez ve bulgunun sınırlı etkisi ele alır);
  - dış CSS ve üst üste binen nesnelerle gizlenmiş metin;
  - sentetik setin gerçek makale performansını ölçmemesi.
