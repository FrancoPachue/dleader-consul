using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DLeader.Consul.Exceptions
{
    public class ServiceRegistrationException : ConsulException
    {
        public ServiceRegistrationException(string message) : base(message) { }
        public ServiceRegistrationException(string message, Exception inner) : base(message, inner) { }
    }
}
