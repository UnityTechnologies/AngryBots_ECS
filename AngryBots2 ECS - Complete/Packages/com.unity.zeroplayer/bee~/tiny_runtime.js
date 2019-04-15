function ready() {
	try {
		run();
	} catch(e) {
		// Suppress the JS throw message that corresponds to Dots unwinding the call stack to run the application. 
		if (e !== 'run') throw e;
	}
}
