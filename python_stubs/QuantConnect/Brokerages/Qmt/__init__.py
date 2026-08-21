import os
import sys

# quantconnect-stubs provides Python files for static analysis, but LEAN must
# resolve this namespace from the QMT .NET assembly at runtime. Temporarily
# hide the stub package while Python.NET imports the real namespace.
package_directory = os.path.dirname(__file__)
while os.path.basename(package_directory) != "QuantConnect":
    package_directory = os.path.dirname(package_directory)
stub_root_directory = os.path.dirname(package_directory)

original_python_paths = sys.path[:]
sys.path.remove(stub_root_directory)

del sys.modules["QuantConnect.Brokerages.Qmt"]
from clr import AddReference

AddReference("QuantConnect.Brokerages.Qmt")
from QuantConnect.Brokerages.Qmt import *

sys.path = original_python_paths
