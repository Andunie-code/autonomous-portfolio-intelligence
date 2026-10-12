# ADR-0007: Sağlayıcıdan bağımsız model yönlendirme; şu an yetkili tek runtime sağlayıcısı Gemini

- Durum: Kabul edildi · Tarih: 2026-10-12 · Revizyon: 2026-10-13 (rev. 3)
- Uygulama durumu: **Planlandı, uygulanmadı**
- İlgili: ADR-0010 (yeterlilik, harcama kontrolü, acil durdurma)

## Bağlam
- Uygulamanın runtime AI çağrıları için şu an yetkili tek sağlayıcı Gemini (D12, geçici).
- Anthropic runtime kullanımı ayrı onay gerektirir (D19).
- Claude Code aboneliğinin uygulamaya runtime erişimi sağladığı varsayılmaz (D13).
- 12.000 TL Gemini kredisi bir finansman kaynağıdır, harcama onayı değildir (D4).

## Karar
1. `IAiProvider` (sağlayıcı başına bir adapter) ve sağlayıcıdan bağımsız `ModelRouter`. İlk ve tek adapter `GeminiProvider` (resmi `Google.GenAI` .NET paketi); başlangıç modeli `gemini-3.5-flash-lite`.
2. **Sağlayıcılar arası fallback yok** (T14). Bir sağlayıcı kesintide ya da nitelik yokken görev, politikasındaki `onUnavailable` davranışına düşer (D20).
3. Nötr ve sığ şemalar; zorunlu tool seçimine dayanılmaz; konuşma geçmişi yalnızca eklenerek büyür. Sağlayıcıya özel optimizasyonlar adapter içinde kalır.
4. Yeni bir sağlayıcı yalnızca **yeni bir adapter ve aynı yeterlilik protokolleriyle** eklenir. Bunun için ayrı onay gerekir (D19, P3).
5. Claude Code'un headless modda uygulamanın AI arka ucu olarak kullanılması kapsam dışıdır.

## Gerekçe
Tek sağlayıcıyla sağlayıcılar arası fallback, karmaşıklık katıp kalite eşdeğerliği varsayımı yaratır. Sağlayıcı bağımsızlığını korumak için adapter sınırı yeterlidir.

## Doğrulanan bilgiler
`sources.md`: Gemini S-G1 ile S-G7 arası (2026-10-12); Anthropic S-A1 ile S-A3 arası (yalnızca referans).

## Riskler
- Structured output ve tool'ların birlikte kullanımı 3.5 Flash-Lite için belgelenmemiş; M4 spike'ında doğrulanacak.
- Gemini, karar etkileyen görevlerde nitelik kazanamayabilir. Sistem LLM'siz modda tam işlevlidir.

## Revizyon geçmişi
- 2026-10-12: Gemini varsayılan sağlayıcı.
- 2026-10-13 (rev. 2): Router, yeterlilik, D12 ve D13'ün geçici olması, D19.
- 2026-10-13 (rev. 3): Sağlayıcılar arası fallback ve görev sınıfı başına aylık bütçe kaldırıldı. Harcama kontrolü ADR-0010'a taşındı.
