# ADR-0008: Kontrollü web araştırması (kendi getiricimiz ve karantina altında extraction)

- Durum: Kabul edildi (implementasyon M7) · Tarih: 2026-10-12 · Revizyon: 2026-10-13 (rev. 3: grounding ve URL context yeniden değerlendirildi, T13)
- Uygulama: ADR-0022 ve `m7-protocol.md` (M7 Kapı A, kullanıcı onayı 2026-10-28). S-G3 ve S-G5 2026-10-28'de yeniden doğrulandı; T13'ün dayanağı değişmedi.

## Bağlam
Researcher'ın uzun vadede web'deki teknik ve nicel kaynakları kullanması isteniyor. Riskler: prompt injection, yanıltıcı iddialar, maliyet ve kaynak kullanım şartları. Kullanıcı, "Gemini'nin yeteneklerini kullanabiliyorsak kullanalım" yönünde tercih bildirdi. Bu yüzden grounding ve URL context yeniden değerlendirildi.

## Karar
- Web erişimini deterministik bir **SourceGateway** yapar: allowlist (redirect'ler dahil), robots.txt, rate limit, önbellek ve `SourceDocument` provenance kaydı (URL, zaman, içerik hash'i).
- Ham içeriği yalnızca **tool'suz bir Extractor** okur ve şemalı `Finding[]` üretir. Alıntılar, kendi çıkardığımız metinde deterministik olarak aranarak doğrulanır.
- Extractor yalnızca FindingExtraction görev sınıfı için nitelikli bir model varsa çalışır (ADR-0010). Yoksa web araştırması kapalı kalır.
- Researcher ham metni görmez. Bulgular yalnızca hipotez üretir; hattın hiçbir adımı atlanamaz.
- Keşif ücretsiz akademik arama API'leri ve allowlist ile yapılır (koşulları M7 öncesinde doğrulanacak).
- **Google Search grounding otonom hatta kullanılmaz.** Etkileşimli bir modda kullanımı öneri olarak bekliyor (P2).
- **URL context kullanılmaz.**

## Değerlendirme (rev. 3)
| Seçenek | Artı | Eksi | Sonuç |
|---|---|---|---|
| Otonom hatta Google Search grounding | Güçlü keşif | Şartlar (S-G5): sonuçlar istemi gönderen son kullanıcıya Search Suggestions ile gösterilmeli, saklama kısıtlı, "learn from" yasak. Kalıcı otonom hafızayla bağdaşmıyor (hukuki görüş değil, bizim okumamız). Ücretsiz katmanda yok; kota sonrası ücretli (S-G3) | **Hayır** |
| Etkileşimli grounding (araştırma asistanı ekranı) | Şartlarla uyumlu kullanım mümkün: sonuçlar size gösterilir, hipotezi siz yazarsınız | Ücretli sorgu harcaması, UI işi | **P2** (M9 sonrası, kullanıcı onayıyla) |
| URL context | Getirme ve ayrıştırmayı Gemini yapar | Redirect allowlist'i, içerik hash'i ve alıntı doğrulaması bizden çıkar; içerik zaten girdi token'ı olarak faturalanır (S-G3). Kendi getiricimiz aynı işi kontrollü yapıyor | **Hayır** |
| Kendi getiricimiz + Gemini Extractor | Provenance, allowlist ve doğrulama bizde; Gemini'nin structured output yeteneği kullanılıyor | PDF ve HTML metin çıkarma bizim işimiz | **Evet** |

## Sonuçlar
- Keşif gücü Google aramasından daha sınırlı, ama kontrol, provenance ve maliyet bizde.
- Grounding'in açılması yeni bir ADR, kullanıcı onayı ve şartların yeniden incelenmesini gerektirir.
