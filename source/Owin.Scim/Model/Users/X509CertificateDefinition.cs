namespace Owin.Scim.Model.Users
{
    using Configuration;

    public class X509CertificateDefinition : ScimTypeDefinitionBuilder<X509Certificate>
    {
        public X509CertificateDefinition()
        {
            For(c => c.Value)
                .SetDescription(@"The value of an X.509 certificate.");
        }
    }
}