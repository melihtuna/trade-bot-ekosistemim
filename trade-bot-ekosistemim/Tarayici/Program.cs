using Binance.API.Csharp.Client;
using Binance.API.Csharp.Client.Models.Enums;
using Binance.API.Csharp.Client.Models.Market;
using Binance.API.Csharp.Client.Utils;
using Microsoft.Practices.EnterpriseLibrary.Data;
using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Net.Mail;
using System.Text;
using Trady.Analysis.Indicator;
using Trady.Core;
using Trady.Core.Infrastructure;

namespace Tarayici
{
    class Program
    {
        private static string mail_Adresleri = "Rapor maili almak istediğiniz mail adresini giriniz";

        private static readonly DateTime StartUnixTime = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        private static List<string> _taramaHatalari = new List<string>();

        //Taramadan hariç tutmak istediğiniz sembolleri ekleyebilirsiniz, bu alanı boş bırakabilirsiniz.
        private static string haric_Sembol_Listesi = "BCCUSDT,BCHSVUSDT,VENUSDT,USDSUSDT,USDCUSDT,PAXUSDT,TUSTUSDT,BULLUSDT,BEARUSDT,ETHBULLUSDT,ETHBEARUSDT,EOSBULLUSDT,EOSBEARUSDT,BNBBULLUSDT,BNBBEARUSDT,XRPBULLUSDT,XRPBEARUSDT";

        static void Main(string[] args)
        {
            var baslangic = DateTime.Now;

            //Binance api key ve secret bilgilerinizi girin. Güvenlik sebebi ile hiçbir kaynak/robot için api yetkilerine transfer yetkileri vermeyin.
            var binanceClient = new BinanceClient(
                new ApiClient(
                    "apiKey",
                    "apiSecret"
                ));

            Console.BufferHeight = Int16.MaxValue - 1;

            Tarayici(binanceClient);

            //mail_Adresleri ve MailGonderAsync alanlarındaki mail alanlarını doldurduktan sonra bu kısmı aktif ediniz.
            //Raporla(baslangic, DateTime.Now);
        }

        private static void Tarayici(BinanceClient binanceClient)
        {
            List<string> haricSemboller = haric_Sembol_Listesi.Split(',').ToList();

            //USDT yazan kısma istediğiniz pariteyi ya da sadece bir sembol taramak istiyorsanız adını girmeniz yeterli. Where şartı kullanmazsanız Binance api üzerindeki tüm semboller gelecektir.
            var tickerPrices = binanceClient.GetAllPrices().Result
                .Where(x => x.Symbol.EndsWith("USDT") && !haricSemboller.Contains(x.Symbol))
                .Select(i => new SymbolPrice { Symbol = i.Symbol, Price = i.Price })
                .OrderBy(x => x.Symbol).ToList();

            //Bu alanda istediğiniz zaman periyoduna göre ekleme yapabilirsiniz: ,TimeInterval.Minutes_5,TimeInterval.Minutes_15,TimeInterval.Minutes_30,TimeInterval.Hours_1, TimeInterval.Hours_4,TimeInterval.Days_1
            var zamanList = new[]
            {
                TimeInterval.Hours_1
            };

            //MOST değerleri için istediğiniz denemeyi yapabilirsiniz
            var emaUzunlukList = new[] { 1, 2, 3, 5, 8, 13, 21, 34 };
            var yuzdeList = new[] { 0.01, 0.02, 0.03, 0.05, 0.08, 0.13 };

            TempSil();

            foreach (var coin in tickerPrices)
            {
                Console.Clear();
                foreach (var z in zamanList)
                {
                    try
                    {
                        var candlestick = binanceClient.GetCandleSticks(coin.Symbol, z, limit: 500).Result;
                        //sadece kapanmış olan barları işleme dahil etmek için kontrolden geçiyoruz
                        var candlesticks = candlestick as Candlestick[] ?? candlestick.Where(x => StartUnixTime.AddMilliseconds(x.CloseTime).ToUniversalTime() < DateTime.Now.ToUniversalTime()).ToArray();
                        if (candlesticks.Length <= 360) continue;

                        //=========================================
                        //5 dakikalıkta çizgi bar varmı kontrolü (en yüksek - en düşük fiyatı aynı olan bar çok fazla varsa yükselişler yanıltabilir, volume olmadığı için işlemler gerçekleşmeyebilir)
                        var candlestick5min = binanceClient.GetCandleSticks(coin.Symbol, TimeInterval.Minutes_5, limit: 360).Result;
                        //sadece kapanmış olan barları işleme dahil etmek için kontrolden geçiyoruz
                        var candlesticks5min = candlestick5min as Candlestick[] ?? candlestick5min.Where(x => StartUnixTime.AddMilliseconds(x.CloseTime).ToUniversalTime() < DateTime.Now.ToUniversalTime()).ToArray();
                        if (candlesticks5min.Count(x => x.High == x.Low) > 50) continue;
                        //=========================================

                        //Binance api barlarını Trady barlarına çevirerek indikatör hesaplamalarına giriyoruz
                        List<IOhlcv> tradyCandles = candlesticks.Select(candle => new Candle(StartUnixTime.AddMilliseconds(candle.OpenTime).ToUniversalTime(), candle.Open, candle.High, candle.Low, candle.Close, candle.Volume)).Cast<IOhlcv>().ToList();

                        foreach (var e in emaUzunlukList)
                        {
                            foreach (var y in yuzdeList)
                            {
                                var mostListe = MostDLL.MostHesapla.Hesapla(tradyCandles, e, y);
                                var iftsListe = InverseFisherTransformOnStochastic.IftsHesapla.Hesapla(tradyCandles, 5, 9);
                                var macdListe = MacdHesapla(tradyCandles);

                                if (mostListe == null || iftsListe == null || macdListe == null) continue;

                                List<IslemListe> stratejiSonucListe = StratejiSonucHesapla(coin, z, e, y, mostListe, iftsListe, macdListe);

                                if (stratejiSonucListe == null) continue;

                                var ozet = OzetHesapla(stratejiSonucListe, stratejiSonucListe.Count);

                                Console.WriteLine(ozet.Sembol + " - " + ozet.Periyot + " - MOST" + ozet.MostParametreleri + " KAR ORAN:" + $"{ozet.KarOran:N2}" + " BAR SAYISI:" + ozet.BarSayisi + " İŞLEM SAYISI:" + ozet.IslemSayisi + " BAŞARILI İŞLEM SAYISI:" + ozet.BasariliIslemSayisi);

                                TempKaydet(ozet);
                            }
                        }
                    }
                    catch (Exception e)
                    {
                        // tarama sırasında robot hata alırsa bize bildirmesi için bir listeye ekliyoruz
                        _taramaHatalari.Add("Tarayici Hatasi: " + coin.Symbol + " " + z.GetDescription() + " " + "(" + e.Message + ")");
                    }
                }
            }

            SonucKaydet();
        }

        private static List<decimal> MacdHesapla(List<IOhlcv> tradyCandles)
        {
            if (tradyCandles.Count > 360)
            {
                var mcdList = new MovingAverageConvergenceDivergenceHistogram(tradyCandles, 12, 26, 9);
                var hesapla = mcdList.Compute();

                var sonuc = hesapla.Select(x => Convert.ToDecimal(x.Tick)).ToList();
                sonuc.RemoveRange(0, 140);
                return sonuc;
            }

            return null;
        }

        private static List<IslemListe> StratejiSonucHesapla(SymbolPrice coin, TimeInterval periyot, int emaUzunluk, double yuzde, List<MostDLL.Most> mostListe, List<double> iftsListe, List<decimal> macdListe)
        {
            List<IslemListe> islemListe = new List<IslemListe>();
            var sgIslemSay = 0;
            var sgPozisyon = "SAT";

            for (int i = 4; i < mostListe.Count; i++)
            {
                var itemMost = mostListe[i];
                if (itemMost.MostDurum == "AL")
                {
                    if (sgPozisyon == "SAT")
                    {
                        var iftsOnay = iftsListe[i - 1] >= -0.5 && iftsListe[i - 2] < -0.5 && iftsListe[i - 3] < -0.5;
                        var macdOnay = macdListe[i - 1] > 0;

                        if (iftsOnay && macdOnay)
                        {
                            sgIslemSay++;
                            sgPozisyon = "AL";
                        }
                    }
                }
                else
                {
                    if (sgPozisyon == "AL")
                    {
                        sgPozisyon = "SAT";
                    }
                }

                DateTime utc = itemMost.Bar.DateTime.UtcDateTime;
                islemListe.Add(new IslemListe
                {
                    Sembol = coin.Symbol,
                    MostParametreleri = "(" + emaUzunluk + "," + yuzde * 100 + ")",
                    Periyot = periyot.GetDescription(),
                    AcilisZamani = utc.ToLocalTime(),
                    Durum = sgPozisyon,
                    EmaDeger = itemMost.EmaDegeri,
                    Fiyat = itemMost.Bar.Open,
                    MostDeger = itemMost.MostDegeri,
                    IslemSayisi = sgIslemSay,
                    Bar = itemMost.Bar
                });
            }

            //Son işlemi sat durumuna geçip o anda eğer al durumunda ise kar oranını hesaba dahil ediyoruz
            if (islemListe.Count > 0)
                islemListe.Last().Durum = "SAT";

            return islemListe;
        }

        private static IslemOzet OzetHesapla(List<IslemListe> islemListe, int barSayisi)
        {
            decimal coinAdet = 0;
            decimal sermaye = 100;
            decimal sunuc = 100;

            bool ilkIslemeGirildiMi = false;
            var durum = islemListe.First().Durum;

            //Aşağıdaki işlemlerde /1000 hesaplaması, Binance'ın alım-satım işlemlerinde 0.01% komisyon farkıdır. Farklı oranınız varsa değiştirebilirsiniz.

            foreach (var islem in islemListe)
            {
                if (durum != islem.Durum)
                {
                    if (islem.Durum == "AL")
                    {
                        coinAdet = sunuc / islem.Fiyat;
                        coinAdet = coinAdet - (coinAdet / 1000);
                        ilkIslemeGirildiMi = true;
                    }
                    else
                    {
                        if (ilkIslemeGirildiMi)
                        {
                            sunuc = islem.Fiyat * coinAdet;
                            sunuc = sunuc - (sunuc / 1000);
                        }
                    }

                    durum = islem.Durum;
                }
            }

            var kar = islemListe.Last().IslemSayisi > 0 ? sunuc - sermaye : 0;
            var karOrani = islemListe.Last().IslemSayisi > 0 ? (sunuc - sermaye) / sermaye * 100 : 0;

            return new IslemOzet
            {
                Sembol = islemListe.First().Sembol,
                Kar = kar,
                Sermaye = sermaye,
                MostParametreleri = islemListe.First().MostParametreleri,
                Periyot = islemListe.First().Periyot,
                KarOran = karOrani,
                BarSayisi = barSayisi,
                IslemSayisi = islemListe.Last().IslemSayisi,
                BasariliIslemSayisi = BasariliIslemSayisiGetir(islemListe)
            };
        }

        private static int BasariliIslemSayisiGetir(List<IslemListe> islemListe)
        {
            if (islemListe.Last().IslemSayisi > 0)
            {
                var durum = islemListe.First().Durum;
                decimal alFiyat = 0;
                var basariliIslemSayisi = 0;
                foreach (var t in islemListe)
                {
                    if (t.Durum != durum)
                    {
                        durum = t.Durum;
                        if (durum == "AL")
                        {
                            alFiyat = t.Fiyat;
                        }

                        if (durum == "SAT")
                        {
                            if (t.Fiyat - alFiyat > 0)
                            {
                                basariliIslemSayisi++;
                            }
                        }
                    }
                }

                return basariliIslemSayisi;
            }

            return 0;
        }

        private static void TempSil()
        {
            try
            {
                DatabaseProviderFactory factory = new DatabaseProviderFactory();
                Database db = factory.Create("connStrBot");
                var comm = db.GetSqlStringCommand("TRUNCATE TABLE temp_RehberTablo");
                db.ExecuteNonQuery(comm);
            }
            catch (Exception e)
            {
                Console.WriteLine(e);
            }
        }

        private static void TempKaydet(IslemOzet islemOzet)
        {
            try
            {
                DatabaseProviderFactory factory = new DatabaseProviderFactory();
                Database db = factory.Create("connStrBot");
                var comm = db.GetSqlStringCommand("INSERT INTO temp_RehberTablo (Sembol,Periyot,MostParametreleri,KarOran,BarSayisi,IslemSayisi,BasariliIslemSayisi) VALUES (@Sembol,@Periyot,@MostParametreleri,@KarOran,@BarSayisi,@IslemSayisi,@BasariliIslemSayisi)");
                db.AddInParameter(comm, "Sembol", DbType.String, islemOzet.Sembol);
                db.AddInParameter(comm, "Periyot", DbType.String, islemOzet.Periyot);
                db.AddInParameter(comm, "MostParametreleri", DbType.String, islemOzet.MostParametreleri);
                db.AddInParameter(comm, "KarOran", DbType.Decimal, islemOzet.KarOran);
                db.AddInParameter(comm, "BarSayisi", DbType.Int32, islemOzet.BarSayisi);
                db.AddInParameter(comm, "IslemSayisi", DbType.Int32, islemOzet.IslemSayisi);
                db.AddInParameter(comm, "BasariliIslemSayisi", DbType.Int32, islemOzet.BasariliIslemSayisi);
                db.ExecuteNonQuery(comm);
            }
            catch (Exception e)
            {
                Console.WriteLine(e);
            }
        }

        private static void SonucKaydet()
        {
            try
            {
                DatabaseProviderFactory factory = new DatabaseProviderFactory();
                Database db = factory.Create("connStrBot");
                var query = @"SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED; 
                            TRUNCATE TABLE RehberTablo 
                            INSERT INTO RehberTablo (Sembol,Periyot,MostParametreleri,KarOran,BarSayisi,IslemSayisi,BasariliIslemSayisi)
							SELECT Sembol,Periyot,MostParametreleri,KarOran,BarSayisi,IslemSayisi,BasariliIslemSayisi FROM temp_RehberTablo";

                var comm = db.GetSqlStringCommand(query);
                db.ExecuteNonQuery(comm);
            }
            catch (Exception e)
            {
                Console.WriteLine(e);
            }
        }

        private static void Raporla(DateTime baslangic, DateTime bitis)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("<p>Başlangıç: " + baslangic + " | Bitiş: " + bitis + " | İşlem Süresi: " +
                        $"{(bitis - baslangic).TotalMinutes:N0}" + " dakika</p>");

            sb.Append("<h3>Rehber tablo tamamlandı.</h3>");

            if (_taramaHatalari.Count > 0)
            {
                sb.Append("<h3>Taramada Oluşan Hatalar</h3>");
                sb.Append("<ul>");
                foreach (var t in _taramaHatalari)
                {
                    sb.Append("<li>" + t + "</li>");
                }
                sb.Append("</ul>");
            }

            string mailListesi = mail_Adresleri;
            string[] alicilar = mailListesi.Split(',');
            List<string> liste = alicilar.ToList();

            MailGonderAsync(liste, "Rehber Oluşturan Tarayıcı Raporu - " + baslangic.ToString("dd-MM-yyyy HH:mm"), sb.ToString(), System.Net.Mail.MailPriority.High);
        }

        public static void MailGonderAsync(List<string> aliciMailAdresleri, string epostaKonusu, string epostaIcerigi, System.Net.Mail.MailPriority oncelikSeviyesi)
        {
            try
            {
                int port = 587;
                string sunucu = "smtp.gmail.com";
                string sifre = "Gmail Şifreniz";
                string kullaniciAd = "Gmail Adresiniz";
                bool sslAktifMi = true;

                SmtpClient smtpClient = new SmtpClient(sunucu, port);
                smtpClient.Credentials = new System.Net.NetworkCredential(kullaniciAd, sifre);
                smtpClient.EnableSsl = sslAktifMi;

                System.Net.Mail.MailMessage mm = new System.Net.Mail.MailMessage
                {
                    From = new MailAddress(kullaniciAd, epostaKonusu, System.Text.Encoding.UTF8),
                    Priority = oncelikSeviyesi
                };

                foreach (var alici in aliciMailAdresleri)
                    mm.To.Add(alici);
                mm.SubjectEncoding = System.Text.ASCIIEncoding.GetEncoding("UTF-8");
                mm.BodyEncoding = System.Text.ASCIIEncoding.GetEncoding("UTF-8");
                mm.Subject = epostaKonusu;
                mm.Body = epostaIcerigi;
                mm.IsBodyHtml = true;
                smtpClient.Send(mm);

                Console.WriteLine("==================== BİTTİ ====================");
            }
            catch (Exception ex)
            {
                Console.WriteLine("Hata" + ex);
            }
        }
    }

    public class IslemOzet
    {
        public string Sembol { get; set; }
        public string Periyot { get; set; }
        public string MostParametreleri { get; set; }
        public decimal Sermaye { get; set; }
        public decimal Kar { get; set; }
        public decimal KarOran { get; set; }
        public int BarSayisi { get; set; }
        public int IslemSayisi { get; set; }
        public int BasariliIslemSayisi { get; set; }
    }

    public class IslemListe
    {
        public string Sembol { get; set; }
        public string Periyot { get; set; }
        public string MostParametreleri { get; set; }
        public DateTime AcilisZamani { get; set; }
        public decimal MostDeger { get; set; }
        public decimal EmaDeger { get; set; }
        public string Durum { get; set; }
        public decimal Fiyat { get; set; }
        public int IslemSayisi { get; set; }
        public IOhlcv Bar { get; set; }
    }
}
