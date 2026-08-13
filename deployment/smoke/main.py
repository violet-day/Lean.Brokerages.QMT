from AlgorithmImports import *


class QmtDeploymentSmoke(QCAlgorithm):
    def initialize(self):
        self.set_account_currency("CNY")
        security = self.add_equity("600000", Resolution.TICK, market="china")
        self.debug("[qmt-deployment-smoke] stage=initialize status=ok symbol={0} market={1}".format(
            security.symbol.value,
            security.symbol.id.market))

    def on_data(self, data):
        self.debug("[qmt-deployment-smoke] stage=quote status=ok")
        self.quit("QMT deployment smoke received fake live data")
