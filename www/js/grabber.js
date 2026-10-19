$(document).ready( function(){
	performTranslation();

	var conf_system_section_editor = null;
	var conf_editor_systemControl = null;

	var SYSTEM_AVAILABLE = (window.serverInfo.systemGrabbers != null);

	if(SYSTEM_AVAILABLE) 
	{
		window.schema.systemGrabber.properties.device.enum = ["auto"];
		window.schema.systemGrabber.properties.device.options = {};
		window.schema.systemGrabber.properties.device.options.enum_titles = ["edt_conf_enum_automatic"];
		
		for(var i = 0; i < window.serverInfo.systemGrabbers.modes.length; i++)
		{
			var name = (window.serverInfo.systemGrabbers.modes[i]).toString();			
			window.schema.systemGrabber.properties.device.enum.push(name);
			window.schema.systemGrabber.properties.device.options.enum_titles.push(name);
		}
		
		$('#conf_cont').append(createOptPanel('<svg data-src="svg/capturing_software.svg" fill="currentColor" class="svg-icon"></svg>', $.i18n("edt_conf_system_heading_title"), 'editor_container_system_device', 'btn_submit_systemGrabber'));
		$('#conf_cont').append(createHelpTable(window.schema.systemGrabber.properties, $.i18n("edt_conf_system_heading_title")));
		
		$('#conf_cont').append(createOptPanel('<svg data-src="svg/capturing_software.svg" fill="currentColor" class="svg-icon"></svg>', $.i18n("edt_conf_system_control_heading_title"), 'editor_container_systemControl', 'btn_submit_systemControl'));
		$('#conf_cont').append(createHelpTable(window.schema.systemControl.properties, $.i18n("edt_conf_system_control_heading_title")));			
		
		conf_system_section_editor = createJsonEditor('editor_container_system_device', { systemGrabber : window.schema.systemGrabber}, true, true);
		conf_editor_systemControl = createJsonEditor('editor_container_systemControl', { systemControl: window.schema.systemControl}, true, true, undefined, true);

		conf_editor_systemControl.on('change',function() {
			(conf_editor_systemControl.validate().length ) ? $('#btn_submit_systemControl').attr('disabled', true) : $('#btn_submit_systemControl').attr('disabled', false);
		});

		$('#btn_submit_systemControl').off().on('click',function() {
			requestWriteConfig(conf_editor_systemControl.getValue());
		});
		
		$('#btn_submit_systemGrabber').off().on('click',function() 
		{
			var systemSaveOptions = conf_system_section_editor.getValue();
			requestWriteConfig(systemSaveOptions);
		});
		
		createHint("intro", $.i18n('conf_grabber_dx11_intro'), "editor_container_system_device");
	}

	removeOverlay();
});
