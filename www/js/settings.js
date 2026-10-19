var storedLang;
var availLang = ['cs','de','en','es','fr','it','nl','pl','ro','sv','vi','ru','tr','zh-CN'];
var availLangText = ['Čeština', 'Deutsch', 'English', 'Español', 'Français', 'Italiano', 'Nederlands', 'Polski', 'Română', 'Svenska', 'Tiếng Việt', 'Русский', 'Türkçe', '汉语'];
//$.i18n.debug = true;

//Change Password
function changePassword(){
	showInfoDialog('changePassword', $.i18n('InfoDialog_changePassword_title'));

	// fill default pw if default is set
	if(window.defaultPasswordIsSet)
		$('#oldPw').val('overglow')

	$('#id_btn_ok').off().on('click',function() {
		var oldPw = $('#oldPw').val();
		var newPw = $('#newPw').val();

		requestChangePassword(oldPw, newPw);

		$('#new_modal_dialog').modal('toggle');
	});



	$('#newPw, #oldPw').off().on('input',function(e) {
		($('#oldPw').val().length >= 8 && $('#newPw').val().length >= 8) ? $('#id_btn_ok').attr('disabled', false) : $('#id_btn_ok').attr('disabled', true);
	});
}

$(document).ready( function() {

	//i18n
	function initTrans(lc){
		if (lc == 'auto')
		{
			$.i18n().load().done(
			function() {
				performTranslation();
			});
		}
		else
		{
			$.i18n().locale = lc;
			$.i18n().load( "i18n", lc ).done(
			function() {
				performTranslation();
			});
		}
	}

	if (storageComp())
	{
		storedLang = getStorage("langcode");
		if (storedLang == null)
		{
			setStorage("langcode", 'auto');
			storedLang = 'auto';
			initTrans(storedLang);
		}
		else
		{
			initTrans(storedLang);
		}
	}
	else
	{
		showInfoDialog('warning', "Can't store settings", "Your browser doesn't support localStorage. You can't save a specific language setting (fallback to 'auto detection') and access level (fallback to 'default'). Some wizards may be hidden. You could still use the webinterface without further issues");
		initTrans('auto');
		storedLang = 'auto';		
		$('#btn_setlang').attr("disabled", true);
	}

	initLanguageSelection();

	// change pw btn
	$('#btn_changePassword').off().on('click',function() {
		changePassword();
	});

	//Lock Ui
	$('#btn_lock_ui').off().on('click',function() {
		requestLogout();
		removeStorage('loginToken', true);
		location.replace('/');
	});	
});
