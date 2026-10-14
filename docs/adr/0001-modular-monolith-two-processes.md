# ADR-0001: Modüler monolit, iki process

- Durum: Kabul edildi · Tarih: 2026-10-12 · Revizyon: 2026-10-13 (topoloji ADR-0009 ile netleştirildi), 2026-10-14 (yerel tek makine, ADR-0013)

## Bağlam
Sistem tek bir kullanıcıya hizmet eden kişisel bir proje. Bununla birlikte "araştırma tarafı emir gönderemez" ve "AI exchange secret'larını göremez" gibi katı yetki sınırları var.

## Karar
Kod tek bir solution içinde modüler bir monolit olarak yazılır. Çalışma zamanında iki uygulama process'i vardır:
- **Trading.Host:** exchange secret'larına sahip tek process.
- **Research.Host:** exchange secret'ı yok, veritabanı yetkileri kısıtlı.

İki process yalnızca **ops PostgreSQL** üzerinden haberleşir.

**2026-10-13 revizyonu:** Holdout verisi ops DB'sinde değil, ayrı bir PostgreSQL instance'ında tutulur. Ona yalnızca üçüncü bir bileşen olan HoldoutEvaluator erişir; Research.Host ise evaluator'a dar bir API üzerinden ulaşır. Ortamlar ve ağlar için bkz. ADR-0009.

**2026-10-14 revizyonu:** VPS yok (D5 rev.). Holdout verisi aynı yerel PostgreSQL instance'ında, `ops`'tan ayrı bir `holdout` veritabanında tutulur; erişim rol, `CONNECT` ve `pg_hba` kurallarıyla sınırlanır. Process ayrımı (Trading.Host, Research.Host, HoldoutEvaluator) aynen geçerlidir. Bkz. ADR-0013.

## Sonuçlar
- Yetki sınırı kod konvansiyonuyla değil; process, ağ ve veritabanı rolleriyle uygulanır.
- Bedeli: ek bir host (ve evaluator için bir container).
- Reddedilen alternatifler: mikroservisler (gereksiz işletim yükü); tek process (yetki sınırı yalnızca koda bağlı kalırdı).
