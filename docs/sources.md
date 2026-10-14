# Doğrulanmış Harici Bilgiler

Son güncelleme: 2026-10-13

Bu belgedeki bilgiler, aksi belirtilmedikçe, belirtilen tarihte resmi kaynaktan okunmuştur. Fiyatlar, limitler ve şartlar değişebilir. Her bilgi, ilgili milestone başlamadan önce yeniden doğrulanır.

## Binance (doğrulama tarihi: 2026-10-12)
| ID | Bilgi | Kaynak |
|---|---|---|
| S-B1 | Spot Testnet REST adresi `https://testnet.binance.vision/api`; API anahtarı GitHub girişiyle alınır | https://developers.binance.com/docs/binance-spot-api-docs/testnet/rest-api/general-api-information |
| S-B2 | Rate limit'ler IP'ye bağlıdır. `X-MBX-USED-WEIGHT-*` header'ı kullanımı gösterir. Limit aşımında 429, ısrar edilirse 418 (2 dakikadan 3 güne kadar IP ban) | aynı |
| S-B3 | Timeout ve 5xx durumunda emrin akıbeti bilinmez; emir motorda gerçekleşmiş olabilir. Önerilen yol durumu sorgulamaktır | aynı |
| S-B4 | Açık bir emirle aynı `newClientOrderId` ile gönderilen yeni emir reddedilir | aynı |
| S-B5 | Emir filtreleri: PRICE_FILTER, LOT_SIZE, NOTIONAL/MIN_NOTIONAL, MARKET_LOT_SIZE, MAX_NUM_ORDERS, PERCENT_PRICE_BY_SIDE | https://developers.binance.com/docs/binance-spot-api-docs/filters |
| S-B6 | Tarihsel kline verisi: günlük ve aylık zip dosyaları, her biri için `.CHECKSUM` (SHA-256). Aralıklar 1s'den 1mo'ya kadar | https://github.com/binance/binance-public-data |
| S-B7 | Spot verisinde 2025-01-01'den itibaren zaman damgaları **mikrosaniye** cinsinden | aynı |

| S-B8 | Arşiv listelemesi: `https://data.binance.vision/?prefix=...` HTML web sayfası döndürür (2026-10-14 M1 Kapı 2 denemesinde gözlendi). Path-style S3 listeleme uç noktası `https://s3-ap-northeast-1.amazonaws.com/data.binance.vision?prefix=...&delimiter=/`; dosyalar `https://data.binance.vision/data/...` üzerinden. **Kaynak: kullanıcı beyanı; S3 uç noktasının yanıtı henüz gözlenmedi** (ilk başarılı listelemede doğrulanır) | Kullanıcı, 2026-10-14 |

**Doğrulanamayanlar (varsayım):** Testnet'in sıfırlanma sıklığı; Testnet emir defterinin ve likiditesinin gerçek piyasayı temsil etmediği (yaygın bilgi, resmi dokümanda bulunamadı).

## Google Gemini (doğrulama tarihi: 2026-10-12)
| ID | Bilgi | Kaynak |
|---|---|---|
| S-G1 | `gemini-3.5-flash-lite` Stable. Girdi limiti 1.048.576 token, çıktı limiti 65.536 token. Desteklenenler: structured outputs, function calling, search grounding, URL context, code execution, thinking, caching, Batch API | https://ai.google.dev/gemini-api/docs/models/gemini-3.5-flash-lite · https://ai.google.dev/gemini-api/docs/models |
| S-G2 | Fiyat (1M token başına): girdi 0,30 $, çıktı 2,50 $; Batch ve Flex: 0,15 $ / 1,25 $; context caching 0,03 $ + saatlik 1,00 $ depolama. Ücretsiz katman mevcut. Fiyat sayfası 2027 için caching fiyat artışlarından söz ediyor (detayları alınmadı) | https://ai.google.dev/gemini-api/docs/pricing |
| S-G3 | Google Search grounding (Gemini 3.5 ve 3.1 Flash-Lite): ücretsiz katmanda **yok**. Ücretli katmanda Gemini 3+ modelleri arasında paylaşılan aylık 5.000 ücretsiz sorgu, sonrası 1.000 sorgu başına 14 $. Gemini 3+ modellerinde faturalama sorgu başına yapılır. URL context aracı ücretsizdir ama getirilen içerik girdi token'ı olarak faturalanır | pricing · https://ai.google.dev/gemini-api/docs/google-search |
| S-G4 | Ücretsiz katmanın RPM, TPM ve RPD değerleri dokümanda listelenmiyor; AI Studio'dan okunmalı. Tier 1 için faturalama hesabı gerekir; 250 $ fatura tavanı ve 10 dakikada 10 $ harcama limiti var. Limitler API anahtarı başına değil proje başına uygulanır | https://ai.google.dev/gemini-api/docs/rate-limits |
| S-G5 | Şartlar: ücretsiz katmanda gönderilen içerik ürünleri geliştirmek için kullanılır ve insanlar tarafından incelenebilir; ücretli katmanda kullanılmaz. Google Search grounding: Search Suggestions son kullanıcıya gösterilmelidir; saklama belirli amaçlarla sınırlıdır; "train on, or otherwise learn from" yasaktır; grounding verisi 30 gün saklanır. Hizmet finansal tavsiye için kullanılmamalıdır | https://ai.google.dev/gemini-api/terms |
| S-G6 | Resmi .NET SDK'sı: `Google.GenAI`. GA ilanı "tüm desteklenen platformlar" için yapılmış, C# ayrıca anılmıyor | https://ai.google.dev/gemini-api/docs/libraries · https://googleapis.github.io/dotnet-genai/ |
| S-G7 | Structured output ile tool'ların (Search, URL context, code execution, function calling) birlikte kullanımı "Gemini 3 serisi" için belirtilmiş; 3.5 ayrıca anılmıyor. Desteklenen JSON Schema bir alt küme; büyük veya derin şemalar reddedilebilir | https://ai.google.dev/gemini-api/docs/structured-output |

**Doğrulanacaklar (varsayım):**
- Structured output ve function calling'in `gemini-3.5-flash-lite` üzerinde birlikte çalışması (M4 spike'ında).
- Ücretsiz katmanın gerçek limitleri (AI Studio'dan).
- Thinking token'larının çıktı fiyatından faturalanıp faturalanmadığı.

**Gemini kredisi (kullanıcı beyanı, 2026-10-13):** 12.000 TL Gemini kredisi mevcut. Kredinin Gemini API ücretli katmanına uygulanıp uygulanmadığı, kapsamı ve son kullanma tarihi **doğrulanmadı** (M4 öncesinde). Kredi bir finansman kaynağıdır, harcama onayı değildir (D4).

## Anthropic: yalnızca gelecekteki bir seçenek için referans (doğrulama tarihi: 2026-10-13)
> Anthropic runtime API kullanımı **yetkili değildir** (D12, D19). Bu bilgiler yalnızca ileride yapılacak bir değerlendirme için kaydedilmiştir ve o zaman yeniden doğrulanmalıdır.

| ID | Bilgi | Kaynak |
|---|---|---|
| S-A1 | Claude Opus 5.5, API kimliği `claude-opus-5-5`. 1M girdi ve 128K çıktı token'ı. Adaptive thinking her zaman açık, varsayılan effort `medium`. Eğitim verisi kesim tarihi Haziran 2026. Emeklilik 2027-09-22'den önce değil | https://platform.claude.com/docs/en/models/overview |
| S-A2 | Opus 5.5 fiyatı (1M token başına): girdi 4 $, çıktı 20 $, 5 dakikalık cache yazma 5 $, cache okuma 0,20 $; Batch 2 $ / 10 $. Uzun bağlam için ek ücret yok. `inference_geo: "us"` 1,1× fiyat | https://platform.claude.com/docs/en/about-claude/pricing |
| S-A3 | Türkiye, Claude API'nin desteklendiği ülkeler arasında | https://www.anthropic.com/supported-countries |

**Doğrulanmayanlar:**
- Opus 5.5 API kısıtları: zorunlu tool seçimi reddi, refusal işleme, append-only thinking kuralı. Kaynak, Claude API referans dokümanı; canlı dokümandan doğrulanmadı.
- C# SDK'sının paket adı.
- Yeni bir organizasyonun rate limit katmanları.
- Claude Code aboneliğinin uygulamaya runtime API erişimi sağlayıp sağlamadığı ya da headless kullanımla ilgili şartları. **Sağlamadığı varsayılır** (D13).

## Henüz doğrulanmayanlar (M7 öncesinde)
- arXiv API ve Semantic Scholar API: kullanım koşulları, rate limit'ler, ücretsiz erişim.
- ~~VPS fiyatları~~ — gerekmiyor (2026-10-14, D5 rev.: VPS yok).
- Binance Spot'ta koruyucu stop emir tipleri ve Testnet desteği (M6 öncesinde; ADR-0014).
