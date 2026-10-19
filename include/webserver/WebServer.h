#pragma once

#ifndef PCH_ENABLED
	#include <QObject>
	#include <QString>
	#include <QJsonDocument>
#endif

#include <utils/Logger.h>
#include <utils/settings.h>

class BonjourServiceRegister;
class FileServer;
class QtHttpServer;
class InstanceManager;
class NetOrigin;
class QtHttpRequest;
class QtHttpReply;

class WebServer : public QObject
{
	Q_OBJECT

	Q_MOC_INCLUDE(<webserver/QtHttpReply.h>)
	Q_MOC_INCLUDE(<webserver/QtHttpClientWrapper.h>)

public:
	WebServer(std::shared_ptr<NetOrigin> netOrigin, const QJsonDocument& config, bool useSsl, QObject* parent = nullptr);
	~WebServer() override;

	void start();
	void stop();

	static bool	portAvailable(quint16& port, const LoggerName& log);

signals:
	void portChanged(quint16 port);

public slots:
	void initServer();
	void onServerStopped();
	void onServerStarted(quint16 port);
	void onServerError(QString msg);
	void handleSettingsUpdate(settings::type type, QJsonDocument config);
	void onInitRequestNeedsReply(QtHttpRequest* request, QtHttpReply* reply);

	quint16 getPort() const { return _port; }

private:
	quint16              _port;
	QJsonDocument        _config;
	bool				 _useSsl;
	LoggerName           _log;
	QString              _baseUrl;
	
	QtHttpServer*        _server;
	std::shared_ptr<NetOrigin> _netOrigin;

	const QString        WEBSERVER_DEFAULT_PATH = ":/www";
	const QString        WEBSERVER_DEFAULT_CRT_PATH = ":/default-crt.pem";
	const QString        WEBSERVER_DEFAULT_KEY_PATH = ":/default-key.pem";
	quint16              WEBSERVER_DEFAULT_PORT = 8090;

	BonjourServiceRegister* _serviceRegister = nullptr;
	std::shared_ptr<FileServer> _staticFileServing = nullptr;

	static std::weak_ptr<FileServer> globalStaticFileServing;
};
