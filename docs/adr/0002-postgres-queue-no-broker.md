# ADR-0002: Mesaj aracısı yerine PostgreSQL kuyruğu ve outbox

- Durum: Kabul edildi · Tarih: 2026-10-12

## Bağlam
Döngü saatlik çalışıyor, iş hacmi düşük ve kalıcılık ile idempotency kritik.

## Karar
İşler PostgreSQL'deki kuyruk tablolarında tutulur ve `FOR UPDATE SKIP LOCKED` ile alınır. Emir gönderimi için transactional outbox kullanılır. Redis, RabbitMQ ve Kafka kullanılmaz.

## Sonuçlar
- Tek bir durum kaynağı olur, ek altyapı gerekmez.
- Yeniden değerlendirme tetikleyicisi: dakika altı gecikme gereksinimi ya da birden fazla tüketici servisi ortaya çıkarsa.
