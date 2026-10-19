#pragma once

#ifndef PCH_ENABLED
	#include <QJsonObject>
	#include <QList>
	#include <QVector>
#endif

#include <utils/settings.h>
#include <utils/Components.h>
#include <api/BaseAPI.h>
#include <base/AccessManager.h>

#include <bonjour/DiscoveryWrapper.h>

class LightingInstance;
class ComponentRegister;
class BonjourBrowserWrapper;
class PriorityMuxer;

class CallbackAPI : public BaseAPI
{
	Q_OBJECT

public:
	CallbackAPI(const LoggerName& log, bool localConnection, QObject* parent);

	void subscribe(QJsonArray subsArr);

protected:
	Image<ColorRgb> _liveImage;

	void stopDataConnections() override = 0;
	void removeSubscriptions() override;
	void addSubscriptions() override;
	bool subscribeFor(const QString& type, bool unsubscribe = false);

signals:
	void SignalCallbackToClient(QJsonObject);

protected slots:
	virtual void handleIncomingColors(const QVector<ColorRgb>& ledValues) = 0;
	virtual void handlerInstanceImageUpdated(const Image<ColorRgb>& image) = 0;

private slots:
	void componentStateHandler(overglow::Components comp, bool state);
	void priorityUpdateHandler();
	void imageToLedsMappingChangeHandler(int mappingType);
	void signalAdjustmentUpdatedHandler(QJsonArray newConfig);
	void settingsChangeHandler(settings::type type, const QJsonDocument& data);
	void ledsConfigChangeHandler(settings::type type, const QJsonDocument& data);
	void instancesListChangedHandler();
	void tokenChangeHandler(const QVector<AccessManager::AuthDefinition>& def);
	void performanceUpdateHandler(const QJsonObject& data);
	void signalDiscoveryFoundServiceHandler(DiscoveryRecord::Service type, QList<DiscoveryRecord> records);

private:
	QStringList _availableCommands;
	QStringList _subscribedCommands;
	void doCallback(const QString cmd, const QVariant data);	
};
