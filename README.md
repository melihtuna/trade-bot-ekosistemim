#Trade Bot Ekosistemim

Bu projede birbiri ile ilişkili robotlar aracılığı ile tarama, alım durumu ve stop takibi yapan işlemlerinin örneğini vereceğim. İşlemlerde kullanılan veri tabanını kurabileceğiniz MsSQL Scriptini de ek olarak ekliyorum. ConnectionString ayarlarınızı kendiniz yapmalısınız.

1 — Tarayıcı: İstediğim paritede istediğim periyotlar için stratejimi tüm sembollerde çalıştırarak backtest sonucunu kaydeden robottur. Bu robotun oluşturduğu rehber tablo işleme girip girmeyeceğim parametreleri belirler. Şu anda Binance üzerinde USDT için 1 saatlik periyotta tarama yapması 6 dakika sürüyor ve her yarım saatte bir kez çalışacak şekilde ayarlı durumda. Bu süre içerisinde yaklaşık 4500 backtest yapıyor. Bu taramayı elle yapmayı hayal edemeyiz sanırım.

2 — Avcı: Oluşan rehber tablo sonuçlarından stratejime bağlı olarak başarılı sayılan kayıtları alarak, şu anda alınabilir mi diye kontrol eder; alınabilir gördüğü işlemleri açar. Bu robot her 5 dakikada bir çalışır ve çalışma süresi yaklaşık 30 saniye sürer.

3 — Tüccar: Açık olan işlemleri her dakika başında kontrol ederek stop olması gereken var mı kontrolü yapar. Buradaki stop’tan kasıt sadece ani düşüş yaşayan işlemler için değil, stratejime göre sat durumuna geçmiş işlemleri de kapsıyor. Robotun çalışması yaklaşık 10 saniye sürüyor.

Ekosistemle ilgili detaylı anlatımı aşağıdan bulabilirsiniz: https://medium.com/@a.melihtuna/trade-bot-ekosistemim-c1698177272c


#Projenin Kullanımı

1- Proje içerisindeki robotlar MSSQL veritabanı ile işlem yapmaktadır. Robotları çalıştırmadan önce BotDbScript.sql scriptini kullanarak sql sunucunuzda veri tabanını oluşturun.

2- 3 projenin de app.config dosyasındaki connectionString alanlarını kendi bağlantınıza göre değiştirin.

3- 3 projenin de kod kısmında mail_Adresleri değişkeni için işlem sonuçlarını mail alacağınız mail adresi girin. Daha sonra Raporla metodlarını yorum satırı olmaktan çıkarın.

4- 3 projenin de kod kısmında MailGonderAsync metoduna gmail adresinizi ve şifrenizi girin. Buradaki amaç robotun 3. maddede belirttiğiniz raporu göndermesi için bir gönderen mail kaydıdır. İkisi için de aynı maili kullanabilirsiniz.

5- Solution'u build edin, NuGet paketlerinin onarılmasını ve yüklenmesini sağlayın.

6- Bir sorun kalmadı ise solution'u kapatabilirsiniz.

7- Tarayıcı, Avcı, Tüccar için Görev Zamanlayıcıdan gereken tanımları yapın. Ben sadece USDT paritelerini taramak için Tarayıcı robotunu yarım saatte bir çalıştırtıyorum, saatlik de yaapbilirsiniz, periyotunuza göre günlük de yapabilirsiniz. Avcı robotunu ben 5 dakikada bir çalıştırtıyorum, Tüccar robotu da dakikada bir çalışıyor. Kendi tanımlarınızı kendinize göre yapabilirsiniz. Çalışma mantıklarını medium yazısında anlatmıştım.


Kullanımla alakalı soracağınız şeyler için: https://twitter.com/crypto_melih
