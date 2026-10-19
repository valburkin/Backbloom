$(document).ready( function() {
	performTranslation();
	
	var conf_editor = null;
	var conf_editor_net = null;
	var conf_editor_mqtt = null;
	
	{
		$('#conf_cont').append(createOptPanel('<svg data-src="svg/general_settings.svg" width="18" height="18" fill="currentColor" class="svg-icon"></svg>', $.i18n("edt_conf_webc_heading_title"), 'editor_container', 'btn_submit_www'));
		$('#conf_cont').append(createHelpTable(window.schema.webConfig.properties, $.i18n("edt_conf_webc_heading_title")));
		
		//network
		$('#conf_cont').append(createOptPanel('<svg data-src="svg/network_services.svg" width="18" height="18" fill="currentColor" class="svg-icon"></svg>', $.i18n("edt_conf_net_heading_title"), 'editor_container_net', 'btn_submit_net'));
		$('#conf_cont').append(createHelpTable(window.schema.network.properties, $.i18n("edt_conf_net_heading_title")));

		$('#conf_cont').append(createOptPanel('<svg data-src="svg/network_services.svg" width="18" height="18" fill="currentColor" class="svg-icon"></svg>', $.i18n("edt_conf_mqtt_heading_title"), 'editor_container_mqtt', 'btn_submit_mqtt'));
		$('#conf_cont').append(createHelpTable(window.schema.mqtt.properties, $.i18n("edt_conf_mqtt_heading_title")));

		//Reorder hardcoded token div at the end
		var tocMoved = $("#tok_desc").detach();
		$('#conf_cont').append(tocMoved);
	}	

	conf_editor = createJsonEditor('editor_container', {
		webConfig : window.schema.webConfig
	}, true, true);
	
	$('#btn_submit_www').off().on('click',function() {
		var val = conf_editor.getValue();
		window.fastReconnect = true;
		window.jsonPort = val.webConfig.port;
		requestWriteConfig(val);
	});
	
	// net
	conf_editor_net = createJsonEditor('editor_container_net', {
		network         : window.schema.network
	}, true, true);

	conf_editor_net.on('change',function() {
		conf_editor_net.validate().length ? $('#btn_submit_net').attr('disabled', true) : $('#btn_submit_net').attr('disabled', false);
	});

	$('#btn_submit_net').off().on('click',function() {
		requestWriteConfig(conf_editor_net.getValue());
	});

	conf_editor.on('change',function() {
		conf_editor.validate().length ? $('#btn_submit').attr('disabled', true) : $('#btn_submit').attr('disabled', false);
	});

	//mqtt
	conf_editor_mqtt = createJsonEditor('editor_container_mqtt', {
		mqtt        : window.schema.mqtt
	}, true, true);

	conf_editor_mqtt.on('change',function() {
		conf_editor_mqtt.validate().length ? $('#btn_submit_mqtt').attr('disabled', true) : $('#btn_submit_mqtt').attr('disabled', false);
	});

	$('#btn_submit_mqtt').off().on('click',function() {
		requestWriteConfig(conf_editor_mqtt.getValue());
	});

	//create introduction
	if(window.showOptHelp)
	{
		createHint("intro", $.i18n('conf_webconfig_label_intro'), "editor_container");
		createHint("intro", $.i18n('conf_network_net_intro'), "editor_container_net");
		createHint("intro", $.i18n('conf_network_mqtt_intro'), "editor_container_mqtt");
		createHint("intro", $.i18n('conf_network_tok_intro'), "tok_desc_cont");
	}

	// Token handling
	function buildTokenList()
	{
		$('.tktbody').html("");
		for(var key in tokenList)
		{
			var lastUse = (tokenList[key].last_use) ? tokenList[key].last_use : "-";
			var btn = '<button id="tok'+tokenList[key].id+'" type="button" class="btn btn-danger">'+$.i18n('general_btn_delete')+'</button>';
			$('.tktbody').append(createTableRow([tokenList[key].comment, lastUse, btn], false, true));
			$('#tok'+tokenList[key].id).off().on('click', handleDeleteToken);
		}
	}

	createTable('tkthead', 'tktbody', 'tktable');
	$('.tkthead').html(createTableRow([$.i18n('conf_network_tok_cidhead'), $.i18n('conf_network_tok_lastuse'), $.i18n('general_btn_delete')], true, true));
	buildTokenList();

	function handleDeleteToken(e)
	{
		var key = e.currentTarget.id.replace("tok","");
		requestTokenDelete(key);
		$('#tok'+key).parent().parent().remove();
		// rm deleted token id
		tokenList = tokenList.filter(function( obj ) {
    		return obj.id !== key;
		});
	}

	$('#btn_create_tok').off().on('click',function() {
		requestToken($('#tok_comment').val())
		$('#tok_comment').val("")
		$('#btn_create_tok').attr('disabled', true)
	});
	$('#tok_comment').off().on('input',function(e) {
		(e.currentTarget.value.length >= 10) ? $('#btn_create_tok').attr('disabled', false) : $('#btn_create_tok').attr('disabled', true);
		if(10-e.currentTarget.value.length >= 1 && 10-e.currentTarget.value.length <= 9)
			$('#tok_chars_needed').html(10-e.currentTarget.value.length + " " + $.i18n('general_chars_needed'))
		else
		$('#tok_chars_needed').html("<br />")
	});
	$(window.serverEvents).off("cmd-authorize-createToken").on("cmd-authorize-createToken", function(event) {
		var val = event.response.info;
		showInfoDialog("newToken",$.i18n('conf_network_tok_diaTitle'),$.i18n('conf_network_tok_diaMsg')+'<br><div style="font-weight:bold">'+val.token+'</div>')
		tokenList.push(val)
		buildTokenList()
	});

	function checkApiTokenState(state)
	{
		if(state == false)
			$("#tok_desc").attr('style', 'display:none')
		else
			$("#tok_desc").removeAttr('style')
	}

	$('#root_network_apiAuth').change(function () {
		var state = $(this).is(":checked");
		checkApiTokenState(state);
	})

	checkApiTokenState(window.serverConfig.network.apiAuth);
	
	var instHeaders = document.getElementsByClassName("card-header");
	Array.prototype.forEach.call(instHeaders, function(instHeader, index) {
		if (instHeader.classList.contains('is-instance'))
			putInstanceName(instHeader);
	});
	
	removeOverlay();
});
