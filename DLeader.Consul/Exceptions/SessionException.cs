using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DLeader.Consul.Exceptions
{
    public class SessionException : ConsulException
    {
        public SessionException(string message) : base(message) { }
        public SessionException(string message, Exception inner) : base(message, inner) { }
    }
}
