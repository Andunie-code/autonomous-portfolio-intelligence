# Autonomous Portfolio Intelligence — Planlama Belgeleri

Durum: **M0 tamamlandı; M1 başlamadı** · Son güncelleme: 2026-10-14 (rev. 5)

M0'da çözüm iskeleti, migration altyapısı, loglama ve mimari testler uygulandı. Sistem yerel tek makinede çalışacak şekilde tasarlanmıştır (ADR-0013). Her milestone ayrı kullanıcı onayıyla başlar.

| Belge | İçerik |
|---|---|
| [requirements.md](requirements.md) | Kararlar (U/T/P), varsayılanlar, kapsam, TBD kaydı, engeller |
| [architecture.md](architecture.md) | Ortamlar, modüller, model yönlendirme, harcama kontrolü, holdout güven sınırı, web araştırması |
| [risk-model.md](risk-model.md) | Risk limitleri, trading kill switch'i ve LLM acil durdurma, hata senaryoları |
| [testing-strategy.md](testing-strategy.md) | Test katmanları, yeterlilik protokolü, H-DEV ve H-ENV holdout testleri |
| [roadmap.md](roadmap.md) | M0–M10 ve kabul kriterleri |
| [sources.md](sources.md) | Doğrulanmış harici bilgiler ve tarihleri |
| [CHANGELOG.md](CHANGELOG.md) | Belge değişikliklerinin geçmişi ve gerekçeleri |
| [adr/](adr/) | 0001–0015 Architecture Decision Record'lar |

## Kurallar
- Karar etiketleri: **U** (kullanıcı onaylı), **T** (teknik lider kararı), **P** (kullanıcı onayı gerektiren öneri), **TBD** (belirleme yöntemiyle birlikte). Ayrıntı için bkz. `requirements.md`.
- Harici bilgi (fiyat, limit, API davranışı) yalnızca `sources.md`'de, kaynak ve tarihle yer alır. İmplementasyondan önce yeniden doğrulanır.
- "Uygulandı" ifadesi yalnızca testi geçmiş kontroller için kullanılır.
- Karar düzeyindeki her belge değişikliği `CHANGELOG.md`'ye yazılır; ayrıntılı geçmiş git'tedir.
