# Autonomous Portfolio Intelligence — Planlama Belgeleri

Durum: **Planlama tamamlandı, M0 onayı bekleniyor** · Son güncelleme: 2026-10-13 (rev. 3)

Kaynak kod, iskelet ve altyapı yok. Uygulama, M0 için ayrıca onay verildikten sonra başlar.

| Belge | İçerik |
|---|---|
| [requirements.md](requirements.md) | Kararlar (U/T/P), varsayılanlar, kapsam, TBD kaydı, engeller |
| [architecture.md](architecture.md) | Ortamlar, modüller, model yönlendirme, harcama kontrolü, holdout güven sınırı, web araştırması |
| [risk-model.md](risk-model.md) | Risk limitleri, trading kill switch'i ve LLM acil durdurma, hata senaryoları |
| [testing-strategy.md](testing-strategy.md) | Test katmanları, yeterlilik protokolü, H-DEV ve H-ENV holdout testleri |
| [roadmap.md](roadmap.md) | M0–M10 ve kabul kriterleri |
| [sources.md](sources.md) | Doğrulanmış harici bilgiler ve tarihleri |
| [CHANGELOG.md](CHANGELOG.md) | Belge değişikliklerinin geçmişi ve gerekçeleri |
| [adr/](adr/) | 0001–0011 Architecture Decision Record'lar |

## Kurallar
- Karar etiketleri: **U** (kullanıcı onaylı), **T** (teknik lider kararı), **P** (kullanıcı onayı gerektiren öneri), **TBD** (belirleme yöntemiyle birlikte). Ayrıntı için bkz. `requirements.md`.
- Harici bilgi (fiyat, limit, API davranışı) yalnızca `sources.md`'de, kaynak ve tarihle yer alır. İmplementasyondan önce yeniden doğrulanır.
- "Uygulandı" ifadesi yalnızca testi geçmiş kontroller için kullanılır.
- Her belge değişikliği `CHANGELOG.md`'ye yazılır (git, M0'da başlayacak).
