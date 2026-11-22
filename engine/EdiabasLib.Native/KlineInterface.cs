// Not upstream code.

namespace EdiabasLib.Native
{
    /// EdInterfaceObd on libkline. The only addition: the SGBD's communication parameters
    /// (xsetpar) are passed down to libkline, so its frame gate knows the concept. The line
    /// settings alone can't tell, because every K-line concept uses Protocol.Uart
    /// (EdInterfaceObd.cs:871).
    public sealed class KlineInterface : EdInterfaceObd
    {
        public KlineInterface(KlineTransport transport)
        {
            Transport = transport;
            transport.Bind(this);
        }

        public KlineTransport Transport { get; }

        public override uint[] CommParameter
        {
            get => base.CommParameter;
            set
            {
                base.CommParameter = value;
                Transport.SetConcept(value);
            }
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing) Transport.Dispose();
        }
    }
}
