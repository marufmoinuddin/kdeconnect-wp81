using System;
using System.Collections;
using Org.BouncyCastle.Asn1.Pkcs;
using Org.BouncyCastle.Asn1.X509;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Operators;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Pkcs;
using Org.BouncyCastle.Security;
using Org.BouncyCastle.X509;
using KDEConnect.WP81.Transport;

namespace KDEConnect.WP81.Security
{
    /// <summary>
    /// Creates and reloads this device's TLS identity.
    ///
    /// The certificate MUST match the reference implementation exactly, because
    /// the desktop reads the certificate's subject display name and compares it
    /// with the deviceId from the identity packet
    /// (kdeconnect-kde core/backends/lan/lanlinkprovider.cpp:691-699):
    ///
    ///   subject/issuer : CN=&lt;deviceId&gt;, O=KDE, OU=KDE Connect
    ///   validity       : notBefore = now - 1 year, notAfter = now + 10 years
    ///   signature      : SHA-512
    ///
    /// (sslhelper.cpp:178-233, kdeconnect-android SslHelper.kt:109-127).
    /// The private key is RSA-2048, matching sslhelper.cpp:84-137.
    /// </summary>
    public static class CertificateProvider
    {
        public const string SignatureAlgorithm = "SHA512withRSA";
        public const int KeyStrengthBits = 2048;

        /// <summary>
        /// Builds a fresh identity for <paramref name="deviceId"/>.
        /// The caller is responsible for persisting the returned DER blobs.
        /// </summary>
        public static TlsIdentity CreateIdentity(string deviceId)
        {
            if (string.IsNullOrEmpty(deviceId))
            {
                throw new ArgumentNullException("deviceId");
            }

            RsaKeyPairGenerator generator = new RsaKeyPairGenerator();
            generator.Init(new KeyGenerationParameters(new SecureRandom(), KeyStrengthBits));
            AsymmetricCipherKeyPair keyPair = generator.GenerateKeyPair();

            X509Name distinguishedName = BuildDistinguishedName(deviceId);

            X509V3CertificateGenerator certificateGenerator = new X509V3CertificateGenerator();
            certificateGenerator.SetSerialNumber(Org.BouncyCastle.Math.BigInteger.ValueOf(1));
            certificateGenerator.SetIssuerDN(distinguishedName);
            certificateGenerator.SetSubjectDN(distinguishedName);
            certificateGenerator.SetNotBefore(DateTime.UtcNow.AddYears(-1));
            certificateGenerator.SetNotAfter(DateTime.UtcNow.AddYears(10));
            certificateGenerator.SetPublicKey(keyPair.Public);

            // Generate(ISignatureFactory) carries the signature algorithm, which
            // is the non-obsolete form in Bouncy Castle 1.8.2.
            ISignatureFactory signatureFactory =
                new Asn1SignatureFactory(SignatureAlgorithm, keyPair.Private);
            X509Certificate certificate = certificateGenerator.Generate(signatureFactory);

            TlsIdentity identity = new TlsIdentity();
            identity.DeviceId = deviceId;
            identity.CertificateDer = certificate.GetEncoded();
            identity.PrivateKeyDer = PrivateKeyInfoFactory.CreatePrivateKeyInfo(keyPair.Private).GetDerEncoded();
            return identity;
        }

        /// <summary>
        /// CN=deviceId, O=KDE, OU=KDE Connect - built from explicit OIDs rather
        /// than a DN string so that spacing and ordering cannot drift.
        /// </summary>
        public static X509Name BuildDistinguishedName(string deviceId)
        {
            ArrayList oids = new ArrayList();
            ArrayList values = new ArrayList();

            oids.Add(X509Name.CN);
            values.Add(deviceId);
            oids.Add(X509Name.O);
            values.Add("KDE");
            oids.Add(X509Name.OU);
            values.Add("KDE Connect");

            return new X509Name(oids, values);
        }

        /// <summary>Re-reads a persisted private key (PKCS#8 DER).</summary>
        public static AsymmetricKeyParameter LoadPrivateKey(byte[] pkcs8Der)
        {
            if (pkcs8Der == null || pkcs8Der.Length == 0)
            {
                throw new ArgumentException("private key is empty", "pkcs8Der");
            }
            PrivateKeyInfo info = PrivateKeyInfo.GetInstance(
                Org.BouncyCastle.Asn1.Asn1Object.FromByteArray(pkcs8Der));
            return PrivateKeyFactory.CreateKey(info);
        }

        /// <summary>Re-reads a persisted certificate (DER).</summary>
        public static X509Certificate LoadCertificate(byte[] certificateDer)
        {
            if (certificateDer == null || certificateDer.Length == 0)
            {
                throw new ArgumentException("certificate is empty", "certificateDer");
            }
            return new X509CertificateParser().ReadCertificate(certificateDer);
        }

        /// <summary>
        /// Extracts the common name from a certificate. This is the value the
        /// desktop compares against deviceId.
        /// </summary>
        public static string GetCommonName(X509Certificate certificate)
        {
            if (certificate == null)
            {
                return string.Empty;
            }
            IList commonNames = certificate.SubjectDN.GetValueList(X509Name.CN);
            if (commonNames == null || commonNames.Count == 0)
            {
                return string.Empty;
            }
            object first = commonNames[0];
            return first == null ? string.Empty : first.ToString();
        }

        /// <summary>True when the certificate covers the given instant.</summary>
        public static bool IsValidAt(X509Certificate certificate, DateTime instantUtc)
        {
            if (certificate == null)
            {
                return false;
            }
            return certificate.IsValid(instantUtc);
        }
    }
}
