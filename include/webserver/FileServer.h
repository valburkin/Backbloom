#pragma once

#include "QtHttpReply.h"

class CgiHandler;
class QtHttpRequest;

class FileServer
{
public:
	FileServer();
	virtual ~FileServer();

	static void setBaseUrl(const QString& url);
	void onRequestNeedsReply(QtHttpRequest* request, QtHttpReply* reply);

private:
	static QString			_baseUrl;
	QHash<QString, QString> _mimeDb;
	LoggerName				_log;	
	QString					_resourcePath;

	void printErrorToReply(QtHttpReply* reply, QtHttpReply::StatusCode code, QString errorMessage);
	QString getMimeName(QString filename);
};
